using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace ClaudeWatch.Core;

/// <summary>A build waiting on the server, as the app is told about it.</summary>
public sealed class AppUpdate
{
    public string Version { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public long Size { get; init; }
    public DateTimeOffset Published { get; init; }

    /// <summary>Megabytes, for the line that tells someone what they are about to pull.</summary>
    public double Megabytes => Math.Round(Size / 1024d / 1024d, 1);
}

public enum UpdateStep
{
    Idle,
    Checking,
    Available,
    Downloading,
    Verifying,
    Ready,
    Installing,
    Failed
}

/// <summary>
/// Keeps the installed app in step with the server without anyone reinstalling
/// anything. It asks what the current build is, fetches it when the customer
/// says so, checks the file is byte-for-byte what the server described, and
/// hands it to the installer, which closes the app and starts the new one.
///
/// Nothing here runs on its own: the app decides when to check and the customer
/// decides when to install.
/// </summary>
public sealed class UpdateClient
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(30) };

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly Version _current;

    public UpdateClient(Version? current = null) => _current = Normalise(current ?? CurrentVersion);

    /// <summary>What this build calls itself, as the installer stamped it.</summary>
    public static Version CurrentVersion
    {
        get
        {
            var name = (Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()).GetName();
            return Normalise(name.Version ?? new Version(0, 0, 0, 0));
        }
    }

    public static string CurrentVersionText
    {
        get
        {
            var v = CurrentVersion;
            return v.Revision > 0 ? $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>Downloads land here, beside the settings, never in the install folder.</summary>
    public static string UpdatesFolder => Path.Combine(AppPaths.Root, "updates");

    public UpdateStep Step { get; private set; } = UpdateStep.Idle;
    public AppUpdate? Available { get; private set; }
    public string Error { get; private set; } = string.Empty;
    public int Percent { get; private set; }
    public DateTimeOffset LastCheck { get; private set; }

    /// <summary>Which of the two answered last time, for the line in Settings.</summary>
    public string Source { get; private set; } = string.Empty;

    /// <summary>The verified installer on disk, once there is one.</summary>
    public string ReadyFile { get; private set; } = string.Empty;

    // ------------------------------------------------------------- checking

    /// <summary>
    /// Asks GitHub first when a repository is set, then the server. Two places
    /// rather than one because either can be unreachable from where the
    /// customer is sitting, and an update that only arrives half the time is
    /// not much of an update.
    ///
    /// Returns the newer build, or null when this one is current or nothing
    /// answered. Never throws: neither being reachable is not a reason for the
    /// app to behave differently.
    /// </summary>
    public async Task<AppUpdate?> CheckAsync(GuardSettings settings, CancellationToken cancel = default)
    {
        Step = UpdateStep.Checking;
        Error = string.Empty;

        var found = await FromGitHubAsync(settings, cancel).ConfigureAwait(false);
        var source = "github";

        if (found is null)
        {
            found = await FromServerAsync(settings.OrdersBaseUrl, cancel).ConfigureAwait(false);
            source = "server";
        }

        LastCheck = DateTimeOffset.UtcNow;

        if (found is null)
        {
            Step = UpdateStep.Idle;
            Available = null;
            return null;
        }

        Source = source;
        Available = found;
        Step = UpdateStep.Available;
        return found;
    }

    /// <summary>
    /// latest.json on a public repository's default branch, read through the
    /// raw file host rather than the API: no token, no rate limit worth
    /// thinking about, and it is a plain CDN.
    ///
    /// A private repository cannot be used here, and deliberately so. Reaching
    /// one needs a token, a token inside the app is readable by anyone holding
    /// a copy of the app, and a leaked token is worse than a visible address.
    /// A private repository belongs behind the server, which keeps its token to
    /// itself and hands customers the file.
    /// </summary>
    private async Task<AppUpdate?> FromGitHubAsync(GuardSettings settings, CancellationToken cancel)
    {
        var repo = (settings.UpdateRepo ?? string.Empty).Trim().Trim('/');

        if (repo.Length == 0 || repo.Count(c => c == '/') != 1)
        {
            return null;
        }

        var branch = string.IsNullOrWhiteSpace(settings.UpdateRepoBranch) ? "main" : settings.UpdateRepoBranch.Trim();
        var url = $"https://raw.githubusercontent.com/{repo}/{branch}/latest.json";

        try
        {
            var body = await Client.GetStringAsync(url, cancel).ConfigureAwait(false);
            var release = JsonSerializer.Deserialize<AppUpdate>(body, Json);

            return Newer(release, GitHubHosts);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            return null;
        }
    }

    private async Task<AppUpdate?> FromServerAsync(string baseUrl, CancellationToken cancel)
    {
        var root = OrdersClient.Normalize(baseUrl);

        if (root.Length == 0)
        {
            return null;
        }

        try
        {
            var body = await Client.GetStringAsync($"{root}/api/app/latest", cancel).ConfigureAwait(false);
            var release = JsonSerializer.Deserialize<AppUpdate>(body, Json);

            return Newer(release, new[] { new Uri(root).Host });
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// An answer worth acting on: a real version, newer than this one, and a
    /// download link on a host we were going to talk to anyway. That last check
    /// is what stops one bad or tampered answer from pointing the installer at
    /// somebody else's file.
    /// </summary>
    private AppUpdate? Newer(AppUpdate? release, IReadOnlyCollection<string> allowed)
    {
        if (release is null
            || string.IsNullOrWhiteSpace(release.Version)
            || string.IsNullOrWhiteSpace(release.Url)
            || string.IsNullOrWhiteSpace(release.Sha256))
        {
            return null;
        }

        if (!Version.TryParse(release.Version, out var offered) || Normalise(offered) <= _current)
        {
            return null;
        }

        if (!Uri.TryCreate(release.Url, UriKind.Absolute, out var link)
            || link.Scheme != Uri.UriSchemeHttps
            || !allowed.Any(host => string.Equals(host, link.Host, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return release;
    }

    /// <summary>Where GitHub actually serves release files from.</summary>
    private static readonly string[] GitHubHosts =
    {
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "raw.githubusercontent.com"
    };

    // ---------------------------------------------------------- downloading

    /// <summary>
    /// Pulls the installer, reporting progress, then checks its hash against
    /// what the server said. A file that does not match is deleted rather than
    /// kept around, because the only thing it could be used for is running it.
    /// </summary>
    public async Task<bool> DownloadAsync(IProgress<int>? progress = null, CancellationToken cancel = default)
    {
        var release = Available;

        if (release is null)
        {
            return false;
        }

        Step = UpdateStep.Downloading;
        Error = string.Empty;
        Percent = 0;
        ReadyFile = string.Empty;

        var target = Path.Combine(UpdatesFolder, $"SafeChat-Setup-{release.Version}.exe");

        try
        {
            Directory.CreateDirectory(UpdatesFolder);
            Sweep(keep: release.Version);

            using var response = await Client
                .GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, cancel)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? release.Size;
            var read = 0L;
            var buffer = new byte[128 * 1024];

            await using (var source = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false))
            await using (var file = File.Create(target))
            {
                int got;
                while ((got = await source.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, got), cancel).ConfigureAwait(false);
                    read += got;

                    if (total > 0)
                    {
                        var percent = (int)Math.Clamp(read * 100 / total, 0, 100);
                        if (percent != Percent)
                        {
                            Percent = percent;
                            progress?.Report(percent);
                        }
                    }
                }
            }

            Step = UpdateStep.Verifying;

            if (!Verify(target, release.Sha256))
            {
                TryDelete(target);
                Step = UpdateStep.Failed;
                Error = "checksum";
                return false;
            }

            ReadyFile = target;
            Percent = 100;
            progress?.Report(100);
            Step = UpdateStep.Ready;
            return true;
        }
        catch (OperationCanceledException)
        {
            TryDelete(target);
            Step = UpdateStep.Available;
            Percent = 0;
            return false;
        }
        catch (Exception ex)
        {
            TryDelete(target);
            Step = UpdateStep.Failed;
            Error = ex.Message;
            return false;
        }
    }

    /// <summary>True when the file on disk is exactly what the server described.</summary>
    public static bool Verify(string path, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            var actual = Convert.ToHexString(sha.ComputeHash(stream));

            return string.Equals(actual, expected.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // ----------------------------------------------------------- installing

    /// <summary>
    /// Starts the installer and leaves. It is installed for every user on the
    /// machine, so Windows asks for approval once; declining that is an answer
    /// rather than a failure, and the app carries on with the build it has.
    ///
    /// The caller shuts the app down straight after this returns true. The
    /// installer closes anything still holding a file, replaces the folder and
    /// starts the new build as the signed-in user, not as an administrator.
    /// </summary>
    public bool Install()
    {
        if (ReadyFile.Length == 0 || !File.Exists(ReadyFile))
        {
            return false;
        }

        Step = UpdateStep.Installing;

        try
        {
            var start = new ProcessStartInfo(ReadyFile)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS"
            };

            Process.Start(start);
            return true;
        }
        catch (Exception ex)
        {
            // 1223 is the customer saying no to the Windows prompt.
            Step = UpdateStep.Ready;
            Error = ex is System.ComponentModel.Win32Exception { NativeErrorCode: 1223 } ? "declined" : ex.Message;
            return false;
        }
    }

    public void Dismiss()
    {
        Step = UpdateStep.Idle;
        Available = null;
        Percent = 0;
    }

    // --------------------------------------------------------------- tidying

    /// <summary>
    /// Old installers are tens of megabytes each. One is kept while it is being
    /// installed, and everything else goes.
    /// </summary>
    public static void Sweep(string? keep = null)
    {
        try
        {
            if (!Directory.Exists(UpdatesFolder))
            {
                return;
            }

            foreach (var file in Directory.GetFiles(UpdatesFolder, "SafeChat-Setup-*.exe"))
            {
                if (keep is not null && Path.GetFileName(file).Contains(keep, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                TryDelete(file);
            }
        }
        catch
        {
            // Housekeeping never matters enough to interrupt anything.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A file Windows still has open is swept on the next run.
        }
    }

    /// <summary>1.2 and 1.2.0.0 are the same build, so the gaps are filled before comparing.</summary>
    private static Version Normalise(Version value)
        => new(Math.Max(value.Major, 0), Math.Max(value.Minor, 0),
               Math.Max(value.Build, 0), Math.Max(value.Revision, 0));
}
