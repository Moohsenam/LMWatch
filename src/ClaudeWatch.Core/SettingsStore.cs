using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeWatch.Core;

/// <summary>Loads and saves settings.json, and never throws at the caller.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path;

    public SettingsStore(string? path = null) => _path = path ?? AppPaths.SettingsFile;

    public string Path => _path;

    public GuardSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new GuardSettings();
            }

            var json = File.ReadAllText(_path);
            var settings = JsonSerializer.Deserialize<GuardSettings>(json, Options) ?? new GuardSettings();

            // A file written before services existed keeps its rules: they are
            // read straight out of the old flat shape and become the Claude
            // profile, so nobody's setup resets itself on upgrade.
            Migrate(settings, json);

            return Sanitize(settings);
        }
        catch
        {
            return new GuardSettings();
        }
    }

    public bool Save(GuardSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(Sanitize(settings), Options);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, json);

            if (File.Exists(_path))
            {
                File.Delete(_path);
            }

            File.Move(temp, _path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Settings as a file to hand to another machine. The licence key is left
    /// out on purpose: a key belongs to one computer, and copying it into a
    /// second one would only produce a refusal there and confusion here.
    /// </summary>
    public static string Export(GuardSettings settings)
    {
        var copy = settings.Clone();
        copy.LicenceKey = string.Empty;
        copy.SetupCompleted = true;

        return JsonSerializer.Serialize(copy, Options);
    }

    /// <summary>
    /// Reads an exported file. Throws on anything that is not settings, so the
    /// caller can say so; the current licence key and setup state are kept,
    /// since those describe this machine rather than the file.
    /// </summary>
    public static GuardSettings Import(string json, GuardSettings current)
    {
        var imported = JsonSerializer.Deserialize<GuardSettings>(json, Options)
                       ?? throw new InvalidDataException("That file has no settings in it.");

        Migrate(imported, json);

        imported.LicenceKey = current.LicenceKey;
        imported.SetupCompleted = current.SetupCompleted;

        return Sanitize(imported);
    }

    /// <summary>
    /// Moves the pre-services settings shape into the Claude profile. Reads the
    /// raw document because those properties no longer exist on GuardSettings,
    /// so deserialization has already dropped them.
    /// </summary>
    public static void Migrate(GuardSettings settings, string json)
    {
        if (settings.Services.Count > 0)
        {
            return;
        }

        settings.EnsureServices();

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var claude = settings.ByKey(ServiceProfile.ClaudeKey);
            if (claude is null)
            {
                return;
            }

            claude.ProcessNamePattern = Str(root, "processNamePattern", claude.ProcessNamePattern);
            claude.ExecutablePath = Str(root, "claudeExecutablePath", claude.ExecutablePath);
            claude.RequiredTimeZoneId = Str(root, "requiredTimeZoneId", claude.RequiredTimeZoneId);
            claude.HomeTimeZoneId = Str(root, "homeTimeZoneId", claude.HomeTimeZoneId);
            claude.FirewallRulePath = Str(root, "firewallRulePath", claude.FirewallRulePath);
            claude.EnforceTimeZone = Flag(root, "enforceTimeZone", claude.EnforceTimeZone);
            claude.EnableFirewallKillSwitch = Flag(root, "enableFirewallKillSwitch", claude.EnableFirewallKillSwitch);
            claude.ExtraProcessNames = Strings(root, "extraProcessNames");
            claude.ExcludedProcessNames = Strings(root, "excludedProcessNames");

            // Someone upgrading was only ever watching Claude. Leaving ChatGPT on
            // would start guarding an app they never asked about.
            var chatgpt = settings.ByKey(ServiceProfile.ChatGptKey);
            if (chatgpt is not null)
            {
                chatgpt.Enabled = false;
            }
        }
        catch (JsonException)
        {
            // An unreadable file just keeps the defaults.
        }

        static string Str(JsonElement root, string name, string fallback)
            => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? fallback
                : fallback;

        static bool Flag(JsonElement root, string name, bool fallback)
            => root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : fallback;

        static List<string> Strings(JsonElement root, string name)
        {
            var result = new List<string>();

            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { } text)
                    {
                        result.Add(text);
                    }
                }
            }

            return result;
        }
    }

    public static GuardSettings Sanitize(GuardSettings settings)
    {
        settings.RefreshSeconds = Math.Clamp(settings.RefreshSeconds, 0.5, 60);
        settings.VpnMissTolerance = Math.Clamp(settings.VpnMissTolerance, 1, 30);
        settings.GraceSeconds = Math.Clamp(settings.GraceSeconds, 0, 300);
        settings.LogRetentionDays = Math.Clamp(settings.LogRetentionDays, 1, 365);

        settings.EnsureServices();

        foreach (var profile in settings.Services)
        {
            var fallback = profile.Key == ServiceProfile.ChatGptKey
                ? ServiceProfile.ChatGpt().ProcessNamePattern
                : GuardSettings.DefaultProcessPattern;

            if (string.IsNullOrWhiteSpace(profile.ProcessNamePattern) || !VpnDetector.IsValidRegex(profile.ProcessNamePattern))
            {
                profile.ProcessNamePattern = fallback;
            }

            if (string.IsNullOrWhiteSpace(profile.Accent))
            {
                profile.Accent = profile.Key == ServiceProfile.ChatGptKey ? "#10A37F" : "#D97757";
            }

            profile.ExtraProcessNames = Clean(profile.ExtraProcessNames);
            profile.ExcludedProcessNames = Clean(profile.ExcludedProcessNames);
        }

        // Never leave the window pointing at a service that is gone.
        if (settings.ByKey(settings.ActiveServiceKey) is null)
        {
            settings.ActiveServiceKey = settings.Services[0].Key;
        }

        if (string.IsNullOrWhiteSpace(settings.VpnAdapterPattern) || !VpnDetector.IsValidRegex(settings.VpnAdapterPattern))
        {
            settings.VpnAdapterPattern = GuardSettings.DefaultVpnPattern;
        }

        if (string.IsNullOrWhiteSpace(settings.AccentColor))
        {
            settings.AccentColor = "#D97757";
        }

        settings.OrdersBaseUrl = NormalizeBaseUrl(settings.OrdersBaseUrl);

        // Two minutes is often enough for anyone; a day is as rare as it gets
        // useful. Outside that someone has edited the file by hand.
        settings.UpdateCheckMinutes = Math.Clamp(settings.UpdateCheckMinutes, 2, 1440);

        settings.UpdateRepo = (settings.UpdateRepo ?? string.Empty).Trim().Trim('/');

        if (string.IsNullOrWhiteSpace(settings.UpdateRepoBranch))
        {
            settings.UpdateRepoBranch = "main";
        }

        if (settings.Language is not ("en" or "fa"))
        {
            settings.Language = "en";
        }

        settings.TrustedAdapters = Clean(settings.TrustedAdapters);
        settings.IgnoredAdapters = Clean(settings.IgnoredAdapters);

        return settings;
    }

    /// <summary>
    /// An address the app can actually call. An empty one falls back to the
    /// service's own server, so an install from before there was a default
    /// picks it up rather than sitting there with no orders page. A bare name
    /// like "safechat.ir" gets https, and a trailing slash is dropped so the
    /// paths appended to it never double up.
    /// </summary>
    public static string NormalizeBaseUrl(string? value)
    {
        var text = OrdersClient.Normalize(value ?? string.Empty);

        return text.Length == 0 ? GuardSettings.DefaultOrdersBaseUrl : text;
    }

    private static List<string> Clean(List<string>? values)
        => (values ?? new List<string>())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
