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
public static class ClaudeFinder
{
    private static readonly string[] ExecutableNames = { "claude.exe", "Claude.exe" };

    public static ClaudeLocation Find()
    {
        foreach (var attempt in new Func<ClaudeLocation?>[]
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
                var found = attempt();
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

    private static ClaudeLocation? FromRunningProcess()
    {
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (!process.ProcessName.StartsWith("claude", StringComparison.OrdinalIgnoreCase))
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

    private static ClaudeLocation? FromUninstallEntries()
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

                if (display is null || display.IndexOf("claude", StringComparison.OrdinalIgnoreCase) < 0)
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
                    var found = NewestExecutableIn(folder);
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

    private static ClaudeLocation? FromProtocolHandler()
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = root.OpenSubKey(@"SOFTWARE\Classes\claude\shell\open\command");
            if (key?.GetValue(null) is not string command || command.Length == 0)
            {
                continue;
            }

            var path = command.StartsWith("\"", StringComparison.Ordinal)
                ? command.Split('"')[1]
                : command.Split(' ')[0];

            if (File.Exists(path))
            {
                return new ClaudeLocation { Path = path, Source = "claude:// handler" };
            }
        }

        return null;
    }

    // ------------------------------------------------- 4. versioned installs

    private static ClaudeLocation? FromVersionedFolders()
    {
        foreach (var root in InstallRoots())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            var direct = NewestExecutableIn(root);
            if (direct is not null)
            {
                return new ClaudeLocation { Path = direct, Source = "install folder" };
            }

            // Squirrel-style layout: app-1.2.3\claude.exe next to Update.exe.
            var versioned = Directory.GetDirectories(root, "app-*")
                .OrderByDescending(VersionKey)
                .Select(NewestExecutableIn)
                .FirstOrDefault(p => p is not null);

            if (versioned is not null)
            {
                return new ClaudeLocation { Path = versioned, Source = "install folder" };
            }
        }

        return null;
    }

    private static IEnumerable<string> InstallRoots()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programsX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        if (local.Length > 0)
        {
            yield return Path.Combine(local, "AnthropicClaude");
            yield return Path.Combine(local, "Programs", "Claude");
            yield return Path.Combine(local, "Programs", "claude");
            yield return Path.Combine(local, "Claude");
        }

        if (roaming.Length > 0)
        {
            yield return Path.Combine(roaming, "Claude");
        }

        if (programs.Length > 0)
        {
            yield return Path.Combine(programs, "Claude");
            yield return Path.Combine(programs, "Anthropic", "Claude");
        }

        if (programsX86.Length > 0)
        {
            yield return Path.Combine(programsX86, "Claude");
        }
    }

    // ------------------------------------------------------ 5. plain guesses

    private static ClaudeLocation? FromKnownPaths()
    {
        foreach (var root in InstallRoots())
        {
            foreach (var name in ExecutableNames)
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

    private static ClaudeLocation? FromStartMenu()
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
                links = Directory.EnumerateFiles(folder, "*claude*.lnk", SearchOption.AllDirectories);
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

    private static string? NewestExecutableIn(string folder)
    {
        foreach (var name in ExecutableNames)
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
