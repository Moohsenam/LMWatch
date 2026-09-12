using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;

namespace ClaudeWatch.Core;

public sealed class ClaudeLocation
{
    public string Path { get; init; } = string.Empty;

    /// <summary>How it turned up, for the log and the About page.</summary>
    public string Source { get; init; } = string.Empty;

    public bool Found => !string.IsNullOrWhiteSpace(Path);
}

/// <summary>
/// Works out where Claude is installed without asking. Six ways are tried in
/// order of certainty, from a process that is running right now down to a
/// Start Menu shortcut.
/// </summary>
/// <summary>What to look for. The strategies are the same for every chat app.</summary>
public sealed class AppHunt
{
    /// <summary>The word that appears in process names, folders and shortcuts.</summary>
    public string Token { get; init; } = "claude";

    /// <summary>Display name, for the folders under Programs.</summary>
    public string Name { get; init; } = "Claude";

    /// <summary>The protocol the installer registers, without the colon.</summary>
    public string Protocol { get; init; } = "claude";

    /// <summary>Publisher folders to look inside, e.g. Programs\Anthropic\Claude.</summary>
    public string[] Publishers { get; init; } = { "Anthropic" };

    public static AppHunt For(string serviceKey) =>
        string.Equals(serviceKey, ServiceProfile.ChatGptKey, StringComparison.OrdinalIgnoreCase)
            ? new AppHunt
            {
                Token = "chatgpt",
                Name = "ChatGPT",
                Protocol = "chatgpt",
                Publishers = new[] { "OpenAI" }
            }
            : new AppHunt();
}

public static class ClaudeFinder
{
    public static ClaudeLocation Find(string serviceKey) => Find(AppHunt.For(serviceKey));

    /// <summary>Both casings, since installers disagree about it.</summary>
    private static string[] ExecutableNames(AppHunt hunt)
        => new[] { hunt.Token + ".exe", hunt.Name + ".exe" };

    public static ClaudeLocation Find() => Find(new AppHunt());

    public static ClaudeLocation Find(AppHunt hunt)
    {
        foreach (var attempt in new Func<AppHunt, ClaudeLocation?>[]
                 {
                     FromRunningProcess,
                     FromUninstallEntries,
                     FromProtocolHandler,
                     FromVersionedFolders,
                     FromKnownPaths,
                     FromStartMenu
                 })
        {
            try
            {
                var found = attempt(hunt);
                if (found is { Found: true } && File.Exists(found.Path))
                {
                    return found;
                }
            }
            catch
            {
                // A source that misbehaves must not stop the others.
            }
        }

        return new ClaudeLocation();
    }

    // -------------------------------------------------------- 1. running now

    private static ClaudeLocation? FromRunningProcess(AppHunt hunt)
    {
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (!process.ProcessName.StartsWith(hunt.Token, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var path = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    return new ClaudeLocation { Path = path, Source = "running process" };
                }
            }
            catch
            {
                // Access to another user's process is refused; skip it.
            }
            finally
            {
                process.Dispose();
            }
        }

        return null;
    }

    // ------------------------------------------------------ 2. installed list

    private static ClaudeLocation? FromUninstallEntries(AppHunt hunt)
    {
        var roots = new (RegistryKey Hive, string Path)[]
        {
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall")
        };

        foreach (var (hive, path) in roots)
        {
            using var parent = hive.OpenSubKey(path);
            if (parent is null)
            {
                continue;
            }

            foreach (var name in parent.GetSubKeyNames())
            {
                using var entry = parent.OpenSubKey(name);
                var display = entry?.GetValue("DisplayName") as string;

                if (display is null || display.IndexOf(hunt.Token, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                if (entry?.GetValue("DisplayIcon") is string icon)
                {
                    var candidate = icon.Split(',')[0].Trim('"', ' ');
                    if (candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(candidate))
                    {
                        return new ClaudeLocation { Path = candidate, Source = "installed programs" };
                    }
                }

                if (entry?.GetValue("InstallLocation") is string folder && Directory.Exists(folder))
                {
                    var found = NewestExecutableIn(folder, hunt);
                    if (found is not null)
                    {
                        return new ClaudeLocation { Path = found, Source = "installed programs" };
                    }
                }
            }
        }

        return null;
    }

    // ---------------------------------------------------- 3. claude:// handler

    private static ClaudeLocation? FromProtocolHandler(AppHunt hunt)
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = root.OpenSubKey($@"SOFTWARE\Classes\{hunt.Protocol}\shell\open\command");
            if (key?.GetValue(null) is not string command || command.Length == 0)
            {
                continue;
            }

            var path = command.StartsWith("\"", StringComparison.Ordinal)
                ? command.Split('"')[1]
                : command.Split(' ')[0];

            if (File.Exists(path))
            {
                return new ClaudeLocation { Path = path, Source = $"{hunt.Protocol}:// handler" };
            }
        }

        return null;
    }

    // ------------------------------------------------- 4. versioned installs

    private static ClaudeLocation? FromVersionedFolders(AppHunt hunt)
    {
        foreach (var root in InstallRoots(hunt))
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            var direct = NewestExecutableIn(root, hunt);
            if (direct is not null)
            {
                return new ClaudeLocation { Path = direct, Source = "install folder" };
            }

            // Squirrel-style layout: app-1.2.3\<app>.exe next to Update.exe.
            var versioned = Directory.GetDirectories(root, "app-*")
                .OrderByDescending(VersionKey)
                .Select(folder => NewestExecutableIn(folder, hunt))
                .FirstOrDefault(p => p is not null);

            if (versioned is not null)
            {
                return new ClaudeLocation { Path = versioned, Source = "install folder" };
            }
        }

        return null;
    }

    private static IEnumerable<string> InstallRoots(AppHunt hunt)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programsX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        if (local.Length > 0)
        {
            yield return Path.Combine(local, "AnthropicClaude");
            yield return Path.Combine(local, "Programs", hunt.Name);
            yield return Path.Combine(local, "Programs", hunt.Token);
            yield return Path.Combine(local, hunt.Name);
        }

        if (roaming.Length > 0)
        {
            yield return Path.Combine(roaming, hunt.Name);
        }

        if (programs.Length > 0)
        {
            yield return Path.Combine(programs, hunt.Name);
            foreach (var publisher in hunt.Publishers)
            {
                yield return Path.Combine(programs, publisher, hunt.Name);
            }
        }

        if (programsX86.Length > 0)
        {
            yield return Path.Combine(programsX86, hunt.Name);
        }
    }

    // ------------------------------------------------------ 5. plain guesses

    private static ClaudeLocation? FromKnownPaths(AppHunt hunt)
    {
        foreach (var root in InstallRoots(hunt))
        {
            foreach (var name in ExecutableNames(hunt))
            {
                var candidate = Path.Combine(root, name);
                if (File.Exists(candidate))
                {
                    return new ClaudeLocation { Path = candidate, Source = "known location" };
                }
            }
        }

        return null;
    }

    // ------------------------------------------------------- 6. Start Menu

    private static ClaudeLocation? FromStartMenu(AppHunt hunt)
    {
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
        };

        foreach (var folder in folders.Where(f => f.Length > 0 && Directory.Exists(f)))
        {
            IEnumerable<string> links;

            try
            {
                links = Directory.EnumerateFiles(folder, $"*{hunt.Token}*.lnk", SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }

            foreach (var link in links)
            {
                var target = ResolveShortcut(link);
                if (!string.IsNullOrWhiteSpace(target) && File.Exists(target) &&
                    target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    return new ClaudeLocation { Path = target, Source = "Start Menu" };
                }
            }
        }

        return null;
    }

    /// <summary>Reads a shortcut's target through the Windows scripting host.</summary>
    private static string ResolveShortcut(string linkPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return string.Empty;
            }

            var shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return string.Empty;
            }

            var shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod,
                null, shell, new object[] { linkPath });

            if (shortcut is null)
            {
                return string.Empty;
            }

            return shortcut.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty,
                null, shortcut, null) as string ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    // ------------------------------------------------------------- helpers

    private static string? NewestExecutableIn(string folder, AppHunt hunt)
    {
        foreach (var name in ExecutableNames(hunt))
        {
            var candidate = Path.Combine(folder, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Sorts app-1.2.10 above app-1.2.9, which a plain text sort would not.</summary>
    private static (int, int, int, string) VersionKey(string folder)
    {
        var name = Path.GetFileName(folder);
        var digits = name.StartsWith("app-", StringComparison.OrdinalIgnoreCase) ? name[4..] : name;
        var parts = digits.Split('.', '-');

        int At(int index) => parts.Length > index && int.TryParse(parts[index], out var value) ? value : 0;

        return (At(0), At(1), At(2), name);
    }
}
