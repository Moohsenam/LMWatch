using System.Diagnostics;
using System.Text;

namespace ClaudeWatch.Core;

/// <summary>
/// The hard kill switch. Stopping a process leaves a gap between the tunnel
/// dropping and the process actually dying; a Windows Firewall rule closes that
/// gap by refusing Claude's outbound traffic outright.
///
/// The rule is created once with administrator rights and then only switched on
/// and off, which the scheduled tasks do without another approval prompt.
/// </summary>
public static class FirewallController
{
    /// <summary>
    /// Every name is per service, because a firewall rule names one executable.
    /// Claude and ChatGPT each get their own rule and their own pair of switches,
    /// so blocking one never touches the other.
    /// </summary>
    public static string RuleNameFor(ServiceProfile profile) => $"SafeChat Block {profile.Name}";

    public static string OnTaskFor(ServiceProfile profile) => $"SafeChat-FirewallOn-{profile.Key}";

    public static string OffTaskFor(ServiceProfile profile) => $"SafeChat-FirewallOff-{profile.Key}";

    /// <summary>True once both tasks exist, meaning switching needs no approval.</summary>
    public static bool IsReady(ServiceProfile profile)
        => PrivilegedHelper.TaskExists(OnTaskFor(profile)) && PrivilegedHelper.TaskExists(OffTaskFor(profile));

    /// <summary>
    /// Creates the (disabled) block rule and the two switches. Asks for
    /// administrator approval once.
    /// </summary>
    public static (bool Ok, string Message) Install(ServiceProfile profile, string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return (false, $"{profile.Name} was not found. Set its location in Settings first.");
        }

        var ruleName = RuleNameFor(profile);
        var onTask = OnTaskFor(profile);
        var offTask = OffTaskFor(profile);
        var netsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe");

        var script = new StringBuilder();
        script.AppendLine("@echo off");

        // Start from a clean slate so a changed Claude path cannot leave a stale rule.
        script.AppendLine($"\"{netsh}\" advfirewall firewall delete rule name=\"{ruleName}\" >nul 2>&1");
        script.AppendLine(
            $"\"{netsh}\" advfirewall firewall add rule name=\"{ruleName}\" " +
            $"dir=out action=block enable=no program=\"{executablePath}\" " +
            $"profile=any description=\"Blocked by SafeChat while the VPN is down\"");

        script.AppendLine(Task(onTask, "yes"));
        script.AppendLine(Task(offTask, "no"));
        script.AppendLine("exit /b 0");

        return PrivilegedHelper.RunElevatedScript(script.ToString(), "firewall setup");

        string Task(string taskName, string enable)
            => "schtasks /create /f /rl highest /sc ONEVENT /ec Application " +
               "/mo \"*[System/EventID=65530]\" " +
               $"/tn \"{taskName}\" " +
               $"/tr \"\\\"{netsh}\\\" advfirewall firewall set rule name=\\\"{ruleName}\\\" new enable={enable}\"";
    }

    public static (bool Ok, string Message) Uninstall(ServiceProfile profile)
    {
        var netsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe");

        var script = new StringBuilder();
        script.AppendLine("@echo off");
        script.AppendLine($"\"{netsh}\" advfirewall firewall delete rule name=\"{RuleNameFor(profile)}\" >nul 2>&1");
        script.AppendLine($"schtasks /delete /f /tn \"{OnTaskFor(profile)}\" >nul 2>&1");
        script.AppendLine($"schtasks /delete /f /tn \"{OffTaskFor(profile)}\" >nul 2>&1");
        script.AppendLine("exit /b 0");

        return PrivilegedHelper.RunElevatedScript(script.ToString(), "firewall removal");
    }

    /// <summary>
    /// Switches the rule. Silent when the tasks are installed. During normal
    /// running <paramref name="allowPrompt"/> stays false, so a missing task
    /// means the switch quietly does nothing rather than throwing an approval
    /// dialog at someone every time their tunnel wobbles.
    /// </summary>
    public static bool Set(ServiceProfile profile, bool blocked, bool allowPrompt = false)
    {
        var taskName = blocked ? OnTaskFor(profile) : OffTaskFor(profile);

        if (PrivilegedHelper.TaskExists(taskName) && PrivilegedHelper.RunTask(taskName))
        {
            return true;
        }

        if (!allowPrompt && !TimeZoneController.IsElevated)
        {
            return false;
        }

        return RunNetshDirect(RuleNameFor(profile), blocked);
    }

    private static bool RunNetshDirect(string ruleName, bool blocked)
    {
        try
        {
            var netsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe");
            var elevated = TimeZoneController.IsElevated;

            var info = new ProcessStartInfo
            {
                FileName = netsh,
                Arguments = $"advfirewall firewall set rule name=\"{ruleName}\" new enable={(blocked ? "yes" : "no")}",
                UseShellExecute = !elevated,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            if (!elevated)
            {
                info.Verb = "runas";
            }

            using var process = Process.Start(info);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(15000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads whether the rule is currently switched on. netsh prints in the
    /// system language, so an unreadable answer is reported as unknown rather
    /// than guessed at.
    /// </summary>
    public static bool? IsBlocking(ServiceProfile profile)
    {
        try
        {
            var netsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe");

            var info = new ProcessStartInfo
            {
                FileName = netsh,
                Arguments = $"advfirewall firewall show rule name=\"{RuleNameFor(profile)}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(info);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(8000);

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                return null;
            }

            foreach (var line in output.Split('\n'))
            {
                var text = line.Trim();
                if (!text.StartsWith("Enabled", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = text.Split(':').LastOrDefault()?.Trim();
                if (string.Equals(value, "Yes", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (string.Equals(value, "No", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
