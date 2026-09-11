using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ClaudeWatch.Core;

public sealed class ClaudeProcess
{
    public int Pid { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public DateTime? Started { get; init; }
    public long MemoryBytes { get; init; }
    public string WindowTitle { get; init; } = string.Empty;

    public string MemoryDisplay => MemoryBytes <= 0
        ? "—"
        : MemoryBytes >= 1024L * 1024 * 1024
            ? $"{MemoryBytes / 1024d / 1024 / 1024:0.0} GB"
            : $"{MemoryBytes / 1024d / 1024:0} MB";

    public string StartedDisplay => Started.HasValue ? Started.Value.ToString("HH:mm:ss") : "—";
}

public sealed class StopResult
{
    public int Requested { get; init; }
    public int Stopped { get; init; }
    public int Remaining { get; init; }
    public IReadOnlyList<string> Failures { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Finds and stops Claude processes. Matching is on the executable name only, the
/// way the original script does it, so a command line that merely mentions Claude
/// is never a target.
/// </summary>
public sealed class ProcessWatcher
{
    private Regex? _compiled;
    private string _compiledPattern = string.Empty;

    public IReadOnlyList<ClaudeProcess> Scan(GuardSettings settings)
    {
        var self = Environment.ProcessId;
        var result = new List<ClaudeProcess>();
        var regex = GetRegex(settings.ProcessNamePattern);

        Process[] all;
        try
        {
            all = Process.GetProcesses();
        }
        catch
        {
            return result;
        }

        foreach (var process in all)
        {
            try
            {
                if (process.Id == self)
                {
                    continue;
                }

                var name = process.ProcessName;
                if (string.IsNullOrEmpty(name) || !IsTarget(name, regex, settings))
                {
                    continue;
                }

                string path = string.Empty;
                DateTime? started = null;
                long memory = 0;
                string title = string.Empty;

                try { path = process.MainModule?.FileName ?? string.Empty; } catch { }
                try { started = process.StartTime; } catch { }
                try { memory = process.WorkingSet64; } catch { }
                try { title = process.MainWindowTitle ?? string.Empty; } catch { }

                result.Add(new ClaudeProcess
                {
                    Pid = process.Id,
                    Name = name,
                    Path = path,
                    Started = started,
                    MemoryBytes = memory,
                    WindowTitle = title
                });
            }
            catch
            {
                // A process can die mid-scan; that is not an error worth surfacing.
            }
            finally
            {
                process.Dispose();
            }
        }

        return result
            .OrderByDescending(p => p.MemoryBytes)
            .ThenBy(p => p.Pid)
            .ToList();
    }

    public bool IsTarget(string processName, GuardSettings settings)
        => IsTarget(processName, GetRegex(settings.ProcessNamePattern), settings);

    private static bool IsTarget(string processName, Regex? regex, GuardSettings settings)
    {
        var bare = System.IO.Path.GetFileNameWithoutExtension(processName);

        if (settings.ExcludedProcessNames.Any(x => x.Equals(bare, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (settings.ExtraProcessNames.Any(x => x.Equals(bare, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return regex is not null && regex.IsMatch(bare);
    }

    /// <summary>Two passes, like the original script: some helpers respawn once.</summary>
    public StopResult StopAll(GuardSettings settings)
    {
        var first = Scan(settings);
        var failures = new List<string>();
        var stopped = 0;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var targets = attempt == 0 ? first : Scan(settings);
            if (targets.Count == 0)
            {
                break;
            }

            foreach (var target in targets)
            {
                if (Stop(target.Pid, out var error))
                {
                    stopped++;
                }
                else if (error is not null)
                {
                    failures.Add($"{target.Name} ({target.Pid}): {error}");
                }
            }

            if (attempt == 0)
            {
                Thread.Sleep(300);
            }
        }

        return new StopResult
        {
            Requested = first.Count,
            Stopped = stopped,
            Remaining = Scan(settings).Count,
            Failures = failures
        };
    }

    public bool Stop(int pid, out string? error)
    {
        error = null;
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(1500);
            return true;
        }
        catch (ArgumentException)
        {
            return true; // already gone
        }
        catch (InvalidOperationException)
        {
            return true; // exited between scan and kill
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private Regex? GetRegex(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return null;
        }

        if (_compiled is not null && _compiledPattern == pattern)
        {
            return _compiled;
        }

        try
        {
            _compiled = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));
            _compiledPattern = pattern;
        }
        catch (ArgumentException)
        {
            _compiled = null;
            _compiledPattern = pattern;
        }

        return _compiled;
    }
}
