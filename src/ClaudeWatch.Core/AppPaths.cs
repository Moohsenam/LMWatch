namespace ClaudeWatch.Core;

/// <summary>Everything the app writes lives in one folder under %APPDATA%.</summary>
public static class AppPaths
{
    public static string Root
    {
        get
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SafeChat");
            Directory.CreateDirectory(root);
            return root;
        }
    }

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string LogFolder
    {
        get
        {
            var folder = Path.Combine(Root, "logs");
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    public static string LogFileFor(DateTime day) => Path.Combine(LogFolder, $"activity-{day:yyyy-MM-dd}.jsonl");
}
