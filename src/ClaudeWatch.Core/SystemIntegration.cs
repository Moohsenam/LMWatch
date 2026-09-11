using System.Diagnostics;
using Microsoft.Win32;

namespace ClaudeWatch.Core;

/// <summary>Start-with-Windows, without a scheduled task or admin rights.</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ClaudeWatch";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool Set(bool enabled, bool startMinimized)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key is null)
            {
                return false;
            }

            if (!enabled)
            {
                key.DeleteValue(ValueName, false);
                return true;
            }

            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe))
            {
                return false;
            }

            var command = startMinimized ? $"\"{exe}\" --minimized" : $"\"{exe}\"";
            key.SetValue(ValueName, command, RegistryValueKind.String);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>Launches the Claude desktop app, finding it first if need be.</summary>
public static class ClaudeLauncher
{
    /// <summary>Kept for callers that only want the path.</summary>
    public static string Detect() => ClaudeFinder.Find().Path;

    public static (bool Ok, string Message) Launch(GuardSettings settings)
    {
        var path = settings.ClaudeExecutablePath;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            path = Detect();

            if (!string.IsNullOrWhiteSpace(path))
            {
                settings.ClaudeExecutablePath = path;
            }
        }

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return (false, "Claude was not found. Set its location in Settings.");
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            return (true, path);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}

public static class Shell
{
    public static void OpenFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
        }
        catch
        {
        }
    }

    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
        }
    }
}
