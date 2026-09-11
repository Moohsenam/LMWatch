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
            var settings = JsonSerializer.Deserialize<GuardSettings>(json, Options);
            return Sanitize(settings ?? new GuardSettings());
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

    public static GuardSettings Sanitize(GuardSettings settings)
    {
        settings.RefreshSeconds = Math.Clamp(settings.RefreshSeconds, 0.5, 60);
        settings.VpnMissTolerance = Math.Clamp(settings.VpnMissTolerance, 1, 30);
        settings.GraceSeconds = Math.Clamp(settings.GraceSeconds, 0, 300);
        settings.LogRetentionDays = Math.Clamp(settings.LogRetentionDays, 1, 365);

        if (string.IsNullOrWhiteSpace(settings.ProcessNamePattern) || !VpnDetector.IsValidRegex(settings.ProcessNamePattern))
        {
            settings.ProcessNamePattern = GuardSettings.DefaultProcessPattern;
        }

        if (string.IsNullOrWhiteSpace(settings.VpnAdapterPattern) || !VpnDetector.IsValidRegex(settings.VpnAdapterPattern))
        {
            settings.VpnAdapterPattern = GuardSettings.DefaultVpnPattern;
        }

        if (string.IsNullOrWhiteSpace(settings.AccentColor))
        {
            settings.AccentColor = "#D97757";
        }

        if (settings.Language is not ("en" or "fa"))
        {
            settings.Language = "en";
        }

        settings.TrustedAdapters = Clean(settings.TrustedAdapters);
        settings.IgnoredAdapters = Clean(settings.IgnoredAdapters);
        settings.ExtraProcessNames = Clean(settings.ExtraProcessNames);
        settings.ExcludedProcessNames = Clean(settings.ExcludedProcessNames);

        return settings;
    }

    private static List<string> Clean(List<string>? values)
        => (values ?? new List<string>())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
