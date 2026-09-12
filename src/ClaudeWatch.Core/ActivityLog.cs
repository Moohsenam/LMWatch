using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeWatch.Core;

public enum ActivityKind
{
    Info,
    Good,
    Warning,
    Alert
}

/// <summary>
/// Stable names for the handful of events worth counting. Titles are written
/// for people and get translated and reworded; these never change, so a day's
/// summary can be counted from them years later.
/// </summary>
public static class ActivityCode
{
    public const string Stopped = "stopped";
    public const string Unprotected = "unprotected";
    public const string VpnUp = "vpn_up";
    public const string VpnDown = "vpn_down";
    public const string TimeZone = "timezone";
    public const string Blocked = "blocked";
    public const string Unblocked = "unblocked";
    public const string Paused = "paused";
}

public sealed class ActivityEvent
{
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
    public ActivityKind Kind { get; init; } = ActivityKind.Info;
    public string Title { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;

    /// <summary>One of <see cref="ActivityCode"/>, or empty for everything else.</summary>
    public string Code { get; init; } = string.Empty;

    [JsonIgnore]
    public string TimeDisplay => At.ToString("HH:mm:ss");

    [JsonIgnore]
    public string DateDisplay => At.ToString("yyyy-MM-dd");
}

/// <summary>One day's tally, for the "today" card on the dashboard.</summary>
public sealed class DaySummary
{
    public DateTime Day { get; init; }
    public int Events { get; init; }
    public int Stops { get; init; }
    public int Unprotected { get; init; }
    public int VpnDrops { get; init; }
    public int TimeZoneChanges { get; init; }
    public int Blocks { get; init; }
    public int Pauses { get; init; }
    public DateTimeOffset? LastIncident { get; init; }

    /// <summary>Nothing went wrong today. The good case, and the common one.</summary>
    public bool Calm => Stops == 0 && Unprotected == 0 && VpnDrops == 0;
}

/// <summary>
/// Keeps the last N events in memory for the Activity screen and appends every one
/// to a per-day JSONL file so a history survives restarts.
/// </summary>
public sealed class ActivityLog
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _gate = new();
    private readonly int _memoryLimit;

    public ActivityLog(int memoryLimit = 500) => _memoryLimit = memoryLimit;

    public event EventHandler<ActivityEvent>? Added;

    public List<ActivityEvent> Items { get; } = new();

    public ActivityEvent Add(ActivityKind kind, string title, string detail = "", string code = "")
    {
        var entry = new ActivityEvent { Kind = kind, Title = title, Detail = detail, Code = code };

        lock (_gate)
        {
            Items.Insert(0, entry);
            while (Items.Count > _memoryLimit)
            {
                Items.RemoveAt(Items.Count - 1);
            }
        }

        Append(entry);
        Added?.Invoke(this, entry);
        return entry;
    }

    private void Append(ActivityEvent entry)
    {
        try
        {
            var line = JsonSerializer.Serialize(entry, Options);
            File.AppendAllText(AppPaths.LogFileFor(entry.At.LocalDateTime.Date), line + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // Logging must never break the guard.
        }
    }

    public void LoadRecent(int days = 3)
    {
        try
        {
            var loaded = new List<ActivityEvent>();

            for (var offset = 0; offset < days; offset++)
            {
                var file = AppPaths.LogFileFor(DateTime.Today.AddDays(-offset));
                if (!File.Exists(file))
                {
                    continue;
                }

                foreach (var line in File.ReadAllLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    try
                    {
                        var entry = JsonSerializer.Deserialize<ActivityEvent>(line, Options);
                        if (entry is not null)
                        {
                            loaded.Add(entry);
                        }
                    }
                    catch
                    {
                        // Skip a corrupt line rather than losing the file.
                    }
                }
            }

            lock (_gate)
            {
                Items.Clear();
                Items.AddRange(loaded.OrderByDescending(e => e.At).Take(_memoryLimit));
            }
        }
        catch
        {
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            Items.Clear();
        }
    }

    /// <summary>
    /// What the guard actually did on one day. Counted from the codes rather
    /// than the titles, so translating the app does not change the numbers.
    /// </summary>
    public DaySummary SummaryFor(DateTime day)
    {
        lock (_gate)
        {
            var mine = Items.Where(e => e.At.LocalDateTime.Date == day.Date).ToList();

            return new DaySummary
            {
                Day = day.Date,
                Events = mine.Count,
                Stops = mine.Count(e => e.Code == ActivityCode.Stopped),
                Unprotected = mine.Count(e => e.Code == ActivityCode.Unprotected),
                VpnDrops = mine.Count(e => e.Code == ActivityCode.VpnDown),
                TimeZoneChanges = mine.Count(e => e.Code == ActivityCode.TimeZone),
                Blocks = mine.Count(e => e.Code == ActivityCode.Blocked),
                Pauses = mine.Count(e => e.Code == ActivityCode.Paused),
                LastIncident = mine
                    .Where(e => e.Kind is ActivityKind.Alert or ActivityKind.Warning)
                    .Select(e => (DateTimeOffset?)e.At)
                    .FirstOrDefault()
            };
        }
    }

    public string ExportText()
    {
        var builder = new StringBuilder();
        builder.AppendLine("SafeChat — activity log");
        builder.AppendLine($"Exported {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine(new string('-', 60));

        lock (_gate)
        {
            foreach (var entry in Items.OrderBy(e => e.At))
            {
                builder.Append(entry.At.ToString("yyyy-MM-dd HH:mm:ss"))
                    .Append("  [").Append(entry.Kind.ToString().ToUpperInvariant().PadRight(7)).Append("]  ")
                    .Append(entry.Title);

                if (!string.IsNullOrWhiteSpace(entry.Detail))
                {
                    builder.Append(" — ").Append(entry.Detail);
                }

                builder.AppendLine();
            }
        }

        return builder.ToString();
    }

    public void Prune(int retentionDays)
    {
        try
        {
            var cutoff = DateTime.Today.AddDays(-Math.Max(1, retentionDays));
            foreach (var file in Directory.GetFiles(AppPaths.LogFolder, "activity-*.jsonl"))
            {
                var stamp = Path.GetFileNameWithoutExtension(file).Replace("activity-", string.Empty);
                if (DateTime.TryParse(stamp, out var day) && day < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
        }
    }
}
