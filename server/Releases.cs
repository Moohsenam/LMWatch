using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeWatch.Orders;

/// <summary>One build of the desktop app, as customers will receive it.</summary>
public sealed class AppRelease
{
    /// <summary>Four numbers at most, as the app reports its own version.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>What changed, shown to the customer before they accept it.</summary>
    public string Notes { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;
    public long Size { get; set; }

    /// <summary>SHA-256 of the installer, lower-case hex.</summary>
    public string Sha256 { get; set; } = string.Empty;

    public DateTimeOffset Published { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Off means the file is kept but never offered. A build that turns out to
    /// be bad is withdrawn this way, and everyone still on the old one stays
    /// there instead of being walked into it.
    /// </summary>
    public bool Live { get; set; } = true;

    /// <summary>Downloads counted since it was published. Rough, and enough.</summary>
    public long Downloads { get; set; }
}

/// <summary>
/// The installers, on disk beside the orders. One folder of files and one
/// index, so a backup is still a copy of the data folder and nothing needs a
/// package registry or a bucket.
/// </summary>
public sealed class ReleaseStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();
    private readonly string _folder;
    private readonly string _indexFile;

    private List<AppRelease> _releases = new();

    public ReleaseStore(string root)
    {
        _folder = Path.Combine(root, "releases");
        Directory.CreateDirectory(_folder);

        _indexFile = Path.Combine(_folder, "releases.json");

        try
        {
            if (File.Exists(_indexFile))
            {
                var text = File.ReadAllText(_indexFile);
                _releases = string.IsNullOrWhiteSpace(text)
                    ? new List<AppRelease>()
                    : JsonSerializer.Deserialize<List<AppRelease>>(text, Json) ?? new List<AppRelease>();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[releases] could not read the index: {ex.Message}");
            _releases = new List<AppRelease>();
        }
    }

    public string Folder => _folder;

    /// <summary>Newest first, by version rather than by upload time.</summary>
    public IReadOnlyList<AppRelease> All()
    {
        lock (_gate)
        {
            return _releases
                .OrderByDescending(r => Parse(r.Version))
                .ToList();
        }
    }

    /// <summary>The one the app is offered, or nothing if none is live.</summary>
    public AppRelease? Latest()
    {
        lock (_gate)
        {
            return _releases
                .Where(r => r.Live && File.Exists(Path.Combine(_folder, r.FileName)))
                .OrderByDescending(r => Parse(r.Version))
                .FirstOrDefault();
        }
    }

    public AppRelease? ByVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        lock (_gate)
        {
            return _releases.FirstOrDefault(
                r => string.Equals(r.Version, version, StringComparison.OrdinalIgnoreCase));
        }
    }

    public string PathOf(AppRelease release) => Path.Combine(_folder, release.FileName);

    /// <summary>
    /// Takes an installer that has already been written to a temporary file,
    /// hashes it, and puts it in place. Re-uploading a version replaces it,
    /// which is what someone rebuilding the same number means by it.
    /// </summary>
    public AppRelease Publish(string version, string notes, string tempFile)
    {
        var clean = CleanVersion(version);
        var name = $"SafeChat-Setup-{clean}.exe";
        var target = Path.Combine(_folder, name);

        var hash = HashOf(tempFile);
        var size = new FileInfo(tempFile).Length;

        File.Move(tempFile, target, overwrite: true);

        lock (_gate)
        {
            var release = _releases.FirstOrDefault(
                r => string.Equals(r.Version, clean, StringComparison.OrdinalIgnoreCase));

            if (release is null)
            {
                release = new AppRelease { Version = clean };
                _releases.Add(release);
            }

            release.Notes = (notes ?? string.Empty).Trim();
            release.FileName = name;
            release.Size = size;
            release.Sha256 = hash;
            release.Published = DateTimeOffset.UtcNow;
            release.Live = true;

            Save();
            return release;
        }
    }

    public AppRelease? SetLive(string version, bool live)
    {
        lock (_gate)
        {
            var release = _releases.FirstOrDefault(
                r => string.Equals(r.Version, version, StringComparison.OrdinalIgnoreCase));

            if (release is null)
            {
                return null;
            }

            release.Live = live;
            Save();
            return release;
        }
    }

    public bool Delete(string version)
    {
        lock (_gate)
        {
            var release = _releases.FirstOrDefault(
                r => string.Equals(r.Version, version, StringComparison.OrdinalIgnoreCase));

            if (release is null)
            {
                return false;
            }

            try
            {
                var path = Path.Combine(_folder, release.FileName);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[releases] could not delete the file: {ex.Message}");
            }

            _releases.Remove(release);
            Save();
            return true;
        }
    }

    public void CountDownload(string version)
    {
        lock (_gate)
        {
            var release = _releases.FirstOrDefault(
                r => string.Equals(r.Version, version, StringComparison.OrdinalIgnoreCase));

            if (release is null)
            {
                return;
            }

            release.Downloads++;
            Save();
        }
    }

    // ----------------------------------------------------------- versions

    /// <summary>
    /// Four numbers, nothing else. The version reaches here from a file name
    /// and from a URL, so anything that is not digits and dots is refused
    /// rather than sanitised into something that still points elsewhere.
    /// </summary>
    public static string CleanVersion(string? value)
    {
        var text = (value ?? string.Empty).Trim();

        if (text.Length is 0 or > 24)
        {
            throw new ArgumentException("A version is between one and twenty-four characters.");
        }

        var parts = text.Split('.');
        if (parts.Length is < 2 or > 4)
        {
            throw new ArgumentException("A version looks like 1.2.3.");
        }

        foreach (var part in parts)
        {
            if (part.Length is 0 or > 6 || !part.All(char.IsAsciiDigit))
            {
                throw new ArgumentException("A version is numbers separated by dots.");
            }
        }

        return string.Join('.', parts.Select(p => int.Parse(p).ToString()));
    }

    public static Version Parse(string? value)
        => System.Version.TryParse((value ?? string.Empty).Trim(), out var parsed)
            ? Normalise(parsed)
            : new Version(0, 0, 0, 0);

    /// <summary>
    /// 1.2 and 1.2.0 are the same build. Version treats the missing parts as
    /// -1, which would sort them apart, so they are filled in before comparing.
    /// </summary>
    private static Version Normalise(Version value)
        => new(Math.Max(value.Major, 0), Math.Max(value.Minor, 0),
               Math.Max(value.Build, 0), Math.Max(value.Revision, 0));

    public static string HashOf(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();

        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private void Save()
    {
        try
        {
            var temp = _indexFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_releases, Json), new UTF8Encoding(false));

            if (File.Exists(_indexFile))
            {
                File.Replace(temp, _indexFile, null);
            }
            else
            {
                File.Move(temp, _indexFile);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[releases] could not write the index: {ex.Message}");
        }
    }
}
