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

    /// <summary>The verified installer on disk, once there is one.</summary>
    public string ReadyFile { get; private set; } = string.Empty;

    // ------------------------------------------------------------- checking

    /// <summary>
    /// Returns the newer build, or null when this one is current, the server
    /// has nothing, or it cannot be reached. Never throws: a server that is
    /// down is not a reason for the app to behave differently.
    /// </summary>
    public async Task<AppUpdate?> CheckAsync(string baseUrl, CancellationToken cancel = default)
    {
        var root = OrdersClient.Normalize(baseUrl);

        if (root.Length == 0)
        {
            return null;
        }

        Step = UpdateStep.Checking;
        Error = string.Empty;

        try
        {
            var body = await Client.GetStringAsync($"{root}/api/app/latest", cancel).ConfigureAwait(false);
            var release = JsonSerializer.Deserialize<AppUpdate>(body, Json);

            LastCheck = DateTimeOffset.UtcNow;

            if (release is null
                || string.IsNullOrWhiteSpace(release.Version)
                || string.IsNullOrWhiteSpace(release.Url))
            {
                Step = UpdateStep.Idle;
                Available = null;
                return null;
            }

            if (!Version.TryParse(release.Version, out var offered) || Normalise(offered) <= _current)
            {
                Step = UpdateStep.Idle;
                Available = null;
                return null;
            }

            // A link the server hands back has to stay on the server. Anything
            // else would let one bad answer point the installer elsewhere.
            if (!SameHost(root, release.Url))
            {
                Step = UpdateStep.Idle;
                Available = null;
                return null;
            }

            Available = release;
            Step = UpdateStep.Available;
            return release;
        }
        catch (Exception ex)
        {
            Step = UpdateStep.Idle;
            Error = ex.Message;
            return null;
        }
    }

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

    private static bool SameHost(string root, string url)
    {
        return Uri.TryCreate(root, UriKind.Absolute, out var a)
               && Uri.TryCreate(url, UriKind.Absolute, out var b)
               && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
               && b.Scheme == a.Scheme;
    }
}
