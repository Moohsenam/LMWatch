using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ClaudeWatch.Core;

/// <summary>Why protection is or is not running.</summary>
public enum LicenceState
{
    /// <summary>The trial has not been used up yet.</summary>
    Trial,

    /// <summary>A key is active and inside its window.</summary>
    Licensed,

    /// <summary>Trial finished, no key. Protection is off.</summary>
    NeedsKey,

    /// <summary>The key ran out.</summary>
    Expired,

    /// <summary>Bound to a different machine.</summary>
    WrongDevice,

    /// <summary>Switched off by the seller.</summary>
    Revoked,

    /// <summary>The server said keys are not required at all.</summary>
    NotRequired
}

/// <summary>What the app remembers about its licence between runs.</summary>
public sealed class LicenceStatus
{
    public LicenceState State { get; set; } = LicenceState.Trial;

    /// <summary>When this install first ran, which is when the trial starts.</summary>
    public DateTimeOffset InstalledAt { get; set; } = DateTimeOffset.UtcNow;

    public int TrialDays { get; set; } = 3;

    /// <summary>Whether the server wants keys at all. Cached from the last check.</summary>
    public bool KeysRequired { get; set; } = true;

    public DateTimeOffset? ExpiresAt { get; set; }
    public int DaysLeft { get; set; }

    /// <summary>Last time the server confirmed the key. Drives the offline grace.</summary>
    public DateTimeOffset? LastConfirmedAt { get; set; }

    public string LastError { get; set; } = string.Empty;

    /// <summary>Which services the key covers: claude, chatgpt, or both.</summary>
    public string Service { get; set; } = "both";

    public int TrialDaysLeft(DateTimeOffset now)
        => Math.Max(0, TrialDays - (int)Math.Floor((now - InstalledAt).TotalDays));

    public bool CoversService(string? key)
        => string.Equals(Service, "both", StringComparison.OrdinalIgnoreCase)
           || string.Equals(Service, key, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Decides whether the guard is allowed to run, and talks to the order server
/// about it.
///
/// The rule that matters most here is what happens when the server cannot be
/// reached. A guard that switches itself off because a web server hiccuped would
/// leave a paying customer unprotected at exactly the wrong moment, so a key
/// that was confirmed recently keeps working offline right up to its own expiry
/// date. Only the expiry itself, which the app already knows, can end it.
/// </summary>
public sealed class LicenceClient
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    private readonly string _file;

    public LicenceClient(string? file = null)
        => _file = file ?? Path.Combine(AppPaths.Root, "licence.json");

    public LicenceStatus Status { get; private set; } = new();

    public LicenceStatus Load()
    {
        try
        {
            if (File.Exists(_file))
            {
                var text = File.ReadAllText(_file);
                Status = JsonSerializer.Deserialize<LicenceStatus>(text, Json) ?? new LicenceStatus();
                return Status;
            }
        }
        catch
        {
            // A damaged file starts the trial again rather than locking anyone out.
        }

        Status = new LicenceStatus { InstalledAt = DateTimeOffset.UtcNow };
        Save();
        return Status;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file) ?? ".");
            File.WriteAllText(_file, JsonSerializer.Serialize(Status, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }

    /// <summary>
    /// Whether protection may run right now, worked out from what is already
    /// known. Never makes a network call, so the guard's poll stays instant.
    /// </summary>
    public static bool Allowed(LicenceStatus status, DateTimeOffset now)
    {
        if (!status.KeysRequired)
        {
            return true;
        }

        return status.State switch
        {
            LicenceState.NotRequired => true,
            LicenceState.Licensed => status.ExpiresAt is not { } ends || now < ends,
            LicenceState.Trial => status.TrialDaysLeft(now) > 0,
            _ => false
        };
    }

    public bool Allowed(DateTimeOffset now) => Allowed(Status, now);

    /// <summary>Recomputes the state from dates alone, for when nothing is reachable.</summary>
    public void Refresh(DateTimeOffset now)
    {
        if (!Status.KeysRequired)
        {
            Status.State = LicenceState.NotRequired;
            return;
        }

        if (Status.State == LicenceState.Licensed && Status.ExpiresAt is { } ends)
        {
            Status.DaysLeft = Math.Max(0, (int)Math.Ceiling((ends - now).TotalDays));

            if (now >= ends)
            {
                Status.State = LicenceState.Expired;
            }

            return;
        }

        if (Status.State == LicenceState.Trial && Status.TrialDaysLeft(now) <= 0)
        {
            Status.State = LicenceState.NeedsKey;
        }
    }

    /// <summary>Asks the server how long the trial is and whether keys are needed.</summary>
    public async Task<bool> FetchTermsAsync(string baseUrl)
    {
        var address = OrdersClient.Normalize(baseUrl);
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        try
        {
            using var response = await Http.GetAsync(address.TrimEnd('/') + "/api/licence/terms");
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;

            if (root.TryGetProperty("trialDays", out var days) && days.TryGetInt32(out var value))
            {
                Status.TrialDays = Math.Clamp(value, 0, 90);
            }

            if (root.TryGetProperty("keysRequired", out var required)
                && required.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                Status.KeysRequired = required.GetBoolean();
            }

            Save();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Activates a key, or re-confirms the one already stored. The same call does
    /// both: the server binds on first sight and reports on every sight after.
    /// </summary>
    public async Task<(bool Ok, string Error)> CheckAsync(string baseUrl, string code, string deviceId, string deviceName)
    {
        var address = OrdersClient.Normalize(baseUrl);

        if (string.IsNullOrWhiteSpace(address))
        {
            return (false, "no_server");
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return (false, "no_key");
        }

        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                code,
                deviceId,
                deviceName
            });

            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync(address.TrimEnd('/') + "/api/licence", content);

            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var ok = root.TryGetProperty("ok", out var okValue) && okValue.ValueKind == JsonValueKind.True;
            var error = root.TryGetProperty("error", out var errorValue) && errorValue.ValueKind == JsonValueKind.String
                ? errorValue.GetString() ?? string.Empty
                : string.Empty;

            if (ok)
            {
                Status.State = LicenceState.Licensed;
                Status.LastConfirmedAt = DateTimeOffset.UtcNow;
                Status.LastError = string.Empty;

                if (root.TryGetProperty("expiresAt", out var expires)
                    && expires.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(expires.GetString(), out var parsed))
                {
                    Status.ExpiresAt = parsed;
                }

                if (root.TryGetProperty("daysLeft", out var left) && left.TryGetInt32(out var days))
                {
                    Status.DaysLeft = days;
                }

                if (root.TryGetProperty("service", out var service) && service.ValueKind == JsonValueKind.String)
                {
                    Status.Service = service.GetString() ?? "both";
                }

                Save();
                return (true, string.Empty);
            }

            Status.LastError = error;

            // A refused key says nothing about the trial. Typing a code wrongly,
            // or trying one that belongs to someone else, used to end the days
            // the install still had, which is the opposite of what a trial is
            // for. While it is still running, a failed check leaves it alone.
            var trialStillRunning = Status.State == LicenceState.Trial
                                    && Status.TrialDaysLeft(DateTimeOffset.UtcNow) > 0;

            if (!trialStillRunning)
            {
                Status.State = error switch
                {
                    "expired" => LicenceState.Expired,
                    "revoked" => LicenceState.Revoked,
                    "wrong_device" => LicenceState.WrongDevice,
                    _ => Status.State == LicenceState.Licensed ? LicenceState.Licensed : LicenceState.NeedsKey
                };
            }

            Save();
            return (false, error);
        }
        catch (Exception ex)
        {
            // Unreachable is not the same as refused. A key confirmed earlier
            // keeps running until its own expiry.
            Status.LastError = ex.Message;
            Save();
            return (false, "offline");
        }
    }

    /// <summary>
    /// A stable, non-reversible id for this machine. Built from the Windows
    /// machine GUID where available, falling back to the machine and user name,
    /// and hashed before it leaves here so the server never holds anything
    /// identifying.
    /// </summary>
    public static string DeviceId()
    {
        var raw = string.Empty;

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Cryptography", false);
            raw = key?.GetValue("MachineGuid")?.ToString() ?? string.Empty;
        }
        catch
        {
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                raw = Environment.MachineName + "|" + Environment.UserName;
            }
            catch
            {
                raw = "unknown-machine";
            }
        }

        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("safechat:" + raw));
        return Convert.ToHexString(bytes)[..32].ToLowerInvariant();
    }

    public static string DeviceName()
    {
        try
        {
            return Environment.MachineName;
        }
        catch
        {
            return string.Empty;
        }
    }
}
