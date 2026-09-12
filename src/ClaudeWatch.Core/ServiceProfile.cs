namespace ClaudeWatch.Core;

/// <summary>
/// One protected chat app and everything that is specific to it: how to spot its
/// processes, where its program lives, which clock it wants, and its own firewall
/// rule.
///
/// What is deliberately NOT here: the VPN rule, the poll interval and the grace
/// countdown. There is one network and one machine, so those are settings of the
/// app rather than of a service, and duplicating them per service would only
/// create pairs that contradict each other.
/// </summary>
public sealed class ServiceProfile
{
    public const string ClaudeKey = "claude";
    public const string ChatGptKey = "chatgpt";

    /// <summary>claude or chatgpt. Stable; the display name can change freely.</summary>
    public string Key { get; set; } = ClaudeKey;

    public string Name { get; set; } = "Claude";

    /// <summary>Off means this one is not watched and its section is hidden.</summary>
    public bool Enabled { get; set; } = true;

    public string ProcessNamePattern { get; set; } = string.Empty;

    /// <summary>Extra process names to treat as this service (exact, without .exe).</summary>
    public List<string> ExtraProcessNames { get; set; } = new();

    /// <summary>Process names that must never be stopped.</summary>
    public List<string> ExcludedProcessNames { get; set; } = new();

    public string ExecutablePath { get; set; } = string.Empty;

    // ---- the clock rule -------------------------------------------------
    //
    // Windows has one clock, so only the service you are switched to gets to
    // ask for a time zone. Both can still be watched for the VPN rule.

    public bool EnforceTimeZone { get; set; } = true;
    public string RequiredTimeZoneId { get; set; } = "Eastern Standard Time";
    public string HomeTimeZoneId { get; set; } = "Iran Standard Time";

    // ---- the kill switch ------------------------------------------------
    //
    // Per service, because a firewall rule names one executable. Both can be
    // blocked at once with no conflict.

    public bool EnableFirewallKillSwitch { get; set; }

    /// <summary>Which executable the rule was built for, so a move is noticed.</summary>
    public string FirewallRulePath { get; set; } = string.Empty;

    /// <summary>The colour the whole app takes on while this service is selected.</summary>
    public string Accent { get; set; } = "#D97757";

    public ServiceProfile Clone() => new()
    {
        Key = Key,
        Name = Name,
        Enabled = Enabled,
        ProcessNamePattern = ProcessNamePattern,
        ExtraProcessNames = new List<string>(ExtraProcessNames),
        ExcludedProcessNames = new List<string>(ExcludedProcessNames),
        ExecutablePath = ExecutablePath,
        EnforceTimeZone = EnforceTimeZone,
        RequiredTimeZoneId = RequiredTimeZoneId,
        HomeTimeZoneId = HomeTimeZoneId,
        EnableFirewallKillSwitch = EnableFirewallKillSwitch,
        FirewallRulePath = FirewallRulePath,
        Accent = Accent
    };

    /// <summary>The name of the firewall rule this service owns.</summary>
    public string FirewallRuleName => $"SafeChat Block {Name}";

    public static ServiceProfile Claude() => new()
    {
        Key = ClaudeKey,
        Name = "Claude",
        Enabled = true,
        ProcessNamePattern = @"^claude($|[ \-_])",
        Accent = "#D97757"
    };

    public static ServiceProfile ChatGpt() => new()
    {
        Key = ChatGptKey,
        Name = "ChatGPT",
        Enabled = true,
        // The Windows app's process is plain "ChatGPT". The tail allows for
        // installer variants like "ChatGPT-1.2.3" without matching, say,
        // "ChatGPT Exporter".
        ProcessNamePattern = @"^chatgpt($|[ \-_])",
        Accent = "#10A37F"
    };

    public static List<ServiceProfile> Defaults() => new() { Claude(), ChatGpt() };
}
