using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace ClaudeWatch.Core;

public sealed class TimeZoneOption
{
    public string Id { get; init; } = string.Empty;
    public string Display { get; init; } = string.Empty;
    public override string ToString() => Display;
}

public enum TimeZoneChangeOutcome
{
    Changed,
    AlreadyCorrect,
    Declined,
    Failed
}

public sealed class TimeZoneChangeResult
{
    public TimeZoneChangeOutcome Outcome { get; init; }
    public string CurrentId { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public bool Success => Outcome is TimeZoneChangeOutcome.Changed or TimeZoneChangeOutcome.AlreadyCorrect;
}

/// <summary>
/// Reads the system time zone (never needs rights) and changes it (always does).
/// Three ways to change it, tried in order: the elevated scheduled task if the
/// user installed it, a direct call when the app already runs as administrator,
/// and otherwise a single UAC prompt on tzutil itself.
/// </summary>
public sealed class TimeZoneController
{
    public const string WorkTaskName = "SafeChat-SetWorkTimeZone";
    public const string HomeTaskName = "SafeChat-SetHomeTimeZone";

    public string CurrentId
    {
        get
        {
            try
            {
                TimeZoneInfo.ClearCachedData();
                return TimeZoneInfo.Local.Id;
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    public string CurrentDisplay
    {
        get
        {
            try
            {
                TimeZoneInfo.ClearCachedData();
                return TimeZoneInfo.Local.DisplayName;
            }
            catch
            {
                return CurrentId;
            }
        }
    }

    public static bool IsElevated
    {
        get
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return false;
            }

            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    public static IReadOnlyList<TimeZoneOption> List()
    {
        try
        {
            return TimeZoneInfo.GetSystemTimeZones()
                .Select(tz => new TimeZoneOption { Id = tz.Id, Display = tz.DisplayName })
                .ToList();
        }
        catch
        {
            return Array.Empty<TimeZoneOption>();
        }
    }

    public static bool Exists(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public TimeZoneChangeResult Apply(string timeZoneId, GuardSettings settings)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return new TimeZoneChangeResult { Outcome = TimeZoneChangeOutcome.Failed, CurrentId = CurrentId, Message = "No time zone given." };
        }

        if (string.Equals(CurrentId, timeZoneId, StringComparison.OrdinalIgnoreCase))
        {
            return new TimeZoneChangeResult { Outcome = TimeZoneChangeOutcome.AlreadyCorrect, CurrentId = timeZoneId };
        }

        if (settings.UsePrivilegedHelper)
        {
            var taskName = string.Equals(timeZoneId, settings.Active.HomeTimeZoneId, StringComparison.OrdinalIgnoreCase)
                ? HomeTaskName
                : WorkTaskName;

            if (PrivilegedHelper.TaskExists(taskName) && PrivilegedHelper.RunTask(taskName))
            {
                if (WaitForTimeZone(timeZoneId, TimeSpan.FromSeconds(6)))
                {
                    return new TimeZoneChangeResult { Outcome = TimeZoneChangeOutcome.Changed, CurrentId = timeZoneId };
                }
            }
        }

        var tzutil = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tzutil.exe");
        if (!File.Exists(tzutil))
        {
            tzutil = "tzutil.exe";
        }

        try
        {
            var info = new ProcessStartInfo
            {
                FileName = tzutil,
                Arguments = $"/s \"{timeZoneId}\"",
                UseShellExecute = !IsElevated,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            if (!IsElevated)
            {
                info.Verb = "runas";
            }

            using var process = Process.Start(info);
            process?.WaitForExit(30000);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new TimeZoneChangeResult
            {
                Outcome = TimeZoneChangeOutcome.Declined,
                CurrentId = CurrentId,
                Message = "Administrator approval was declined."
            };
        }
        catch (Exception ex)
        {
            return new TimeZoneChangeResult { Outcome = TimeZoneChangeOutcome.Failed, CurrentId = CurrentId, Message = ex.Message };
        }

        if (WaitForTimeZone(timeZoneId, TimeSpan.FromSeconds(3)))
        {
            return new TimeZoneChangeResult { Outcome = TimeZoneChangeOutcome.Changed, CurrentId = timeZoneId };
        }

        return new TimeZoneChangeResult
        {
            Outcome = TimeZoneChangeOutcome.Failed,
            CurrentId = CurrentId,
            Message = "The time zone did not change."
        };
    }

    private bool WaitForTimeZone(string timeZoneId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (string.Equals(CurrentId, timeZoneId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            Thread.Sleep(150);
        }

        return string.Equals(CurrentId, timeZoneId, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Optional one-time setup: two scheduled tasks that run tzutil with the highest
/// privileges, so day-to-day switching never shows a UAC prompt again. Installing
/// or removing them asks for approval once.
/// </summary>
public static class PrivilegedHelper
{
    public static bool TaskExists(string taskName)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/query /tn \"{taskName}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(info);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(8000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsInstalled()
        => TaskExists(TimeZoneController.WorkTaskName) && TaskExists(TimeZoneController.HomeTaskName);

    public static bool RunTask(string taskName)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/run /tn \"{taskName}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(info);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(10000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }


    /// <summary>
    /// Registers everything that needs administrator rights in one elevated step:
    /// the two time-zone switches, the firewall block rule, and the two switches
    /// that turn that rule on and off. Windows asks for approval once, here, and
    /// nothing afterwards ever needs to ask again.
    /// </summary>
    public static (bool Ok, string Message) InstallEverything(GuardSettings settings)
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var tzutil = Path.Combine(system, "tzutil.exe");
        var netsh = Path.Combine(system, "netsh.exe");

        var located = settings.Watched
            .Where(p => !string.IsNullOrWhiteSpace(p.ExecutablePath) && File.Exists(p.ExecutablePath))
            .ToList();

        var withFirewall = located.Count > 0;

        var script = BuildSetupScript(settings, tzutil, netsh, withFirewall);
        var result = RunElevatedScript(script, "setup");

        if (!result.Ok)
        {
            return result;
        }

        return withFirewall
            ? (true, string.Empty)
            : (true, "The clock switches are ready. No chat app was found, so the firewall rules were skipped.");
    }

    /// <summary>
    /// Builds the batch file the elevated step runs. Separated out so the exact
    /// text, quoting and all, can be checked by the project's tests.
    /// </summary>
    public static string BuildSetupScript(
        GuardSettings settings,
        string tzutil,
        string netsh,
        bool withFirewall)
    {
        var script = new StringBuilder();
        script.AppendLine("@echo off");

        // One clock, so the tasks follow whichever service is selected.
        script.AppendLine(TimeZoneTask(TimeZoneController.WorkTaskName, settings.Active.RequiredTimeZoneId));
        script.AppendLine(TimeZoneTask(TimeZoneController.HomeTaskName, settings.Active.HomeTimeZoneId));

        if (withFirewall)
        {
            // Every service that has been located gets its own rule and switches,
            // all inside this one elevated batch, so Windows asks once for the lot.
            foreach (var profile in settings.Watched)
            {
                if (string.IsNullOrWhiteSpace(profile.ExecutablePath))
                {
                    continue;
                }

                var ruleName = FirewallController.RuleNameFor(profile);

                script.AppendLine(
                    $"\"{netsh}\" advfirewall firewall delete rule name=\"{ruleName}\" >nul 2>&1");
                script.AppendLine(
                    $"\"{netsh}\" advfirewall firewall add rule name=\"{ruleName}\" " +
                    $"dir=out action=block enable=no program=\"{profile.ExecutablePath}\" " +
                    $"profile=any description=\"Blocked by SafeChat while a rule is broken\"");

                script.AppendLine(FirewallTask(FirewallController.OnTaskFor(profile), "yes", netsh, ruleName));
                script.AppendLine(FirewallTask(FirewallController.OffTaskFor(profile), "no", netsh, ruleName));
            }
        }

        script.AppendLine("exit /b 0");
        return script.ToString();

        string TimeZoneTask(string taskName, string timeZoneId)
            => "schtasks /create /f /rl highest /sc ONEVENT /ec Application " +
               "/mo \"*[System/EventID=65530]\" " +
               $"/tn \"{taskName}\" /tr \"\\\"{tzutil}\\\" /s \\\"{timeZoneId}\\\"\"";

        string FirewallTask(string taskName, string enable, string netshPath, string ruleName)
            => "schtasks /create /f /rl highest /sc ONEVENT /ec Application " +
               "/mo \"*[System/EventID=65530]\" " +
               $"/tn \"{taskName}\" " +
               $"/tr \"\\\"{netshPath}\\\" advfirewall firewall set rule name=\\\"{ruleName}\\\" new enable={enable}\"";
    }

    public static (bool Ok, string Message) Install(GuardSettings settings)
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var tzutil = Path.Combine(system, "tzutil.exe");

        var script = new StringBuilder();
        script.AppendLine("@echo off");
        script.AppendLine(Line(TimeZoneController.WorkTaskName, settings.Active.RequiredTimeZoneId));
        script.AppendLine(Line(TimeZoneController.HomeTaskName, settings.Active.HomeTimeZoneId));
        script.AppendLine("exit /b 0");

        return RunElevatedScript(script.ToString(), "install");

        // ONEVENT with an event that never arrives gives a task that only ever runs
        // when asked. A dated /sc once trigger would have to be written in the
        // machine's own date format, which varies by locale.
        string Line(string taskName, string timeZoneId)
            => "schtasks /create /f /rl highest /sc ONEVENT /ec Application " +
               "/mo \"*[System/EventID=65530]\" " +
               $"/tn \"{taskName}\" /tr \"\\\"{tzutil}\\\" /s \\\"{timeZoneId}\\\"\"";
    }

    public static (bool Ok, string Message) Uninstall()
    {
        var script = new StringBuilder();
        script.AppendLine("@echo off");
        script.AppendLine($"schtasks /delete /f /tn \"{TimeZoneController.WorkTaskName}\"");
        script.AppendLine($"schtasks /delete /f /tn \"{TimeZoneController.HomeTaskName}\"");
        script.AppendLine("exit /b 0");

        return RunElevatedScript(script.ToString(), "remove");
    }

    internal static (bool Ok, string Message) RunElevatedScript(string content, string verbLabel)
    {
        var path = Path.Combine(Path.GetTempPath(), $"claude-watch-{Guid.NewGuid():N}.cmd");

        try
        {
            File.WriteAllText(path, content, new UTF8Encoding(false));

            var info = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var process = Process.Start(info);
            if (process is null)
            {
                return (false, "Could not start the setup step.");
            }

            process.WaitForExit(60000);
            return process.ExitCode == 0
                ? (true, string.Empty)
                : (false, $"The {verbLabel} step ended with code {process.ExitCode}.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, "Administrator approval was declined.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
