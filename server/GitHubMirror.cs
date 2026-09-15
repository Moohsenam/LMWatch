using System.Net.Http.Headers;
using System.Text.Json;

namespace ClaudeWatch.Orders;

/// <summary>
/// Watches a GitHub repository for new releases and brings them here.
///
/// The repository is allowed to be private, which is the point of doing it on
/// this side rather than in the app. A token that reaches a customer's machine
/// is a token anyone who bought the app can read out of it, so the app is never
/// told where the code lives: it asks this server, and this server does the
/// asking. The installer is copied here once and served from /download like any
/// other build, so a customer's download never touches GitHub either.
/// </summary>
public sealed class GitHubMirror : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Store _store;
    private readonly ReleaseStore _releases;
    private readonly HttpClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _api;
    private Timer? _timer;

    public GitHubMirror(Store store, ReleaseStore releases)
    {
        _store = store;
        _releases = releases;

        // GitHub itself unless something says otherwise, which is how this gets
        // tested and how a GitHub Enterprise server would be pointed at.
        _api = (Environment.GetEnvironmentVariable("CW_GITHUB_API") ?? "https://api.github.com")
            .TrimEnd('/');

        _client = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("SafeChat-Orders");
        _client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public string LastResult { get; private set; } = string.Empty;

    /// <summary>
    /// One word for what happened, so the panel can say it in its own language
    /// rather than showing the log line. LastResult keeps the detail.
    /// </summary>
    public string LastCode { get; private set; } = string.Empty;

    public DateTimeOffset LastCheck { get; private set; }

    /// <summary>Checks a few minutes after start, then every five minutes.</summary>
    public void Start()
        => _timer = new Timer(
            _ => _ = PullAsync(),
            null,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(5));

    /// <summary>
    /// Takes the newest release, if it is newer than what is already here.
    /// Never throws: GitHub being unreachable leaves the current build alone
    /// and says so on the panel.
    /// </summary>
    public async Task<string> PullAsync(bool force = false)
    {
        var config = _store.Config;

        if (!config.GitHubMirror
            || string.IsNullOrWhiteSpace(config.GitHubRepo)
            || string.IsNullOrWhiteSpace(config.GitHubToken))
        {
            return Note("off", "off");
        }

        if (!await _gate.WaitAsync(0).ConfigureAwait(false))
        {
            return Note("busy", "a check is already running");
        }

        var temp = Path.Combine(_releases.Folder, $"github-{Guid.NewGuid():N}.tmp");

        try
        {
            LastCheck = DateTimeOffset.UtcNow;

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{_api}/repos/{config.GitHubRepo.Trim('/')}/releases/latest");

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.GitHubToken);

            using var response = await _client.SendAsync(request).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized => Note("badtoken", "the token was refused"),
                    System.Net.HttpStatusCode.Forbidden => Note("noaccess", "the token cannot read that repository"),
                    System.Net.HttpStatusCode.NotFound => Note("norepo", "no repository by that name, or no release on it yet"),
                    _ => Note("error", $"GitHub answered {(int)response.StatusCode}")
                };
            }

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var tag = Text(root, "tag_name");
            var notes = Text(root, "body");

            if (tag.Length == 0)
            {
                return Note("notag", "that release has no tag");
            }

            if (!force && string.Equals(tag, config.GitHubLastTag, StringComparison.OrdinalIgnoreCase))
            {
                return Note("same", $"already on {tag}");
            }

            string version;
            try
            {
                version = ReleaseStore.CleanVersion(tag.TrimStart('v', 'V'));
            }
            catch (ArgumentException)
            {
                return Note("badtag", $"the tag '{tag}' is not a version like v1.2.3");
            }

            // The .exe on the release is the installer. Anything else attached
            // to it is left where it is.
            var asset = FindInstaller(root);

            if (asset is null)
            {
                return Note("noasset", $"{tag} has no .exe attached to it");
            }

            using var download = new HttpRequestMessage(HttpMethod.Get, asset.Value.Url);
            download.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.GitHubToken);
            // A private repository hands the file over only when asked for the
            // bytes rather than the description of them.
            download.Headers.Accept.Clear();
            download.Headers.Accept.ParseAdd("application/octet-stream");

            using var file = await _client
                .SendAsync(download, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);

            if (!file.IsSuccessStatusCode)
            {
                return Note("error", $"could not download {asset.Value.Name}: {(int)file.StatusCode}");
            }

            await using (var target = File.Create(temp))
            await using (var source = await file.Content.ReadAsStreamAsync().ConfigureAwait(false))
            {
                await source.CopyToAsync(target).ConfigureAwait(false);
            }

            if (new FileInfo(temp).Length < 1024)
            {
                File.Delete(temp);
                return Note("empty", $"{asset.Value.Name} arrived empty");
            }

            var published = _releases.Publish(version, FirstLine(notes), temp);

            _store.SaveConfig(c => c.GitHubLastTag = tag);

            var megabytes = Math.Round(published.Size / 1024d / 1024d, 1);
            return Note("took", $"took {tag} as {published.Version}, {megabytes} MB");
        }
        catch (Exception ex)
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch { /* not worth a second failure */ }
            }

            return Note("error", ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static (string Url, string Name)? FindInstaller(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var name = Text(asset, "name");
            var url = Text(asset, "url");

            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && url.Length > 0)
            {
                return (url, name);
            }
        }

        return null;
    }

    /// <summary>
    /// Release notes can be pages long. The customer sees one line, and a
    /// heading like "## What's new" is not that line: it is the label on the
    /// thing they actually want to read.
    /// </summary>
    private static string FirstLine(string notes)
    {
        var line = (notes ?? string.Empty)
            .Replace("\r", string.Empty)
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.TrimStart('*', '-', '+', ' ').Trim())
            .FirstOrDefault(l => l.Length > 0) ?? string.Empty;

        return line.Length > 160 ? line[..160].TrimEnd() + "..." : line;
    }

    private static string Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private string Note(string code, string message)
    {
        LastCode = code;
        LastResult = message;
        Console.WriteLine($"[github] {message}");
        return message;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _client.Dispose();
        _gate.Dispose();
    }
}
