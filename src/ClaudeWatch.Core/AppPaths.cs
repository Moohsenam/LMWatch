namespace ClaudeWatch.Core;

/// <summary>Everything the app writes lives in one folder under %APPDATA%.</summary>
public static class AppPaths
{
    /// <summary>What the folder was called before the app was renamed.</summary>
    private const string OldFolder = "ClaudeWatch";

    private const string Folder = "SafeChat";

    private static bool _carriedOver;

    public static string Root
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var root = Path.Combine(appData, Folder);
            Directory.CreateDirectory(root);

            CarryOverOnce(appData, root);

            return root;
        }
    }

    /// <summary>
    /// The rename moved this folder, which on its own would have quietly reset
    /// every existing install back to defaults: the settings file would still be
    /// sitting in the old folder, and the app would never look there. So the
    /// first time the new folder is used and is empty, the old one is copied
    /// across. The original is left where it is, so a downgrade still works.
    /// </summary>
    private static void CarryOverOnce(string appData, string root)
    {
        if (_carriedOver)
        {
            return;
        }

        _carriedOver = true;

        try
        {
            var settings = Path.Combine(root, "settings.json");
            if (File.Exists(settings))
            {
                return;
            }

            var old = Path.Combine(appData, OldFolder);
            var oldSettings = Path.Combine(old, "settings.json");

            if (!File.Exists(oldSettings))
            {
                return;
            }

            File.Copy(oldSettings, settings);

            // The activity history is worth keeping too.
            var oldLogs = Path.Combine(old, "logs");
            if (Directory.Exists(oldLogs))
            {
                var newLogs = Path.Combine(root, "logs");
                Directory.CreateDirectory(newLogs);

                foreach (var file in Directory.GetFiles(oldLogs))
                {
                    var target = Path.Combine(newLogs, Path.GetFileName(file));
                    if (!File.Exists(target))
                    {
                        File.Copy(file, target);
                    }
                }
            }
        }
        catch
        {
            // A failed carry-over means defaults, which is survivable. A throw
            // here would stop the app from starting at all, which is not.
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
