using System.Text.Json.Serialization;

namespace ClaudeWatch.Core;

/// <summary>What the guard does when a safety rule is broken.</summary>
public enum EnforcementAction
{
    /// <summary>Stop every matching Claude process.</summary>
    StopClaude = 0,

    /// <summary>Leave the processes alone, only warn.</summary>
    WarnOnly = 1
}

/// <summary>How the guard reacts to a time-zone mismatch.</summary>
public enum TimeZoneMode
{
    /// <summary>Show a prompt and let the user decide each time.</summary>
    Ask = 0,

    /// <summary>Change the time zone as soon as a mismatch appears.</summary>
    Automatic = 1,

    /// <summary>Never change the time zone; only report the mismatch.</summary>
    ReportOnly = 2
}

public enum AppTheme
{
    Dark = 0,
    Light = 1,
    System = 2
}

/// <summary>Everything the user can change. Serialized to settings.json.</summary>
public sealed class GuardSettings
{
    public const string DefaultProcessPattern = @"^claude($|[ \-_])";
    public const string DefaultVpnPattern =
        @"(?i)(vpn|wireguard|wintun|openvpn|tailscale|nordlynx|proton|mullvad|anyconnect|globalprotect|fortinet|zerotier|hiddify|singbox|sing-box|tap[\-_ ]|tun[\-_ ])";

    // ---- protection ---------------------------------------------------
    public bool EnforceVpn { get; set; } = true;
    public bool EnforceTimeZone { get; set; } = true;
    public EnforcementAction Action { get; set; } = EnforcementAction.StopClaude;

    /// <summary>
    /// Consecutive polls with no VPN before the guard switches the time zone back
    /// home. Claude is blocked on the very first miss regardless; this only stops a
    /// momentary network blip from flipping the system clock.
    /// </summary>
    public int VpnMissTolerance { get; set; } = 3;

    public double RefreshSeconds { get; set; } = 2;

    /// <summary>
    /// How long Claude is left running after a rule breaks. Its traffic is cut at
    /// the firewall immediately either way; this is only the window to put the
    /// tunnel back before the process is closed. Zero closes it at once.
    /// </summary>
    public int GraceSeconds { get; set; } = 10;

    /// <summary>Show the full-screen countdown while that window is open.</summary>
    public bool ShowGraceOverlay { get; set; } = true;

    // ---- time zones ---------------------------------------------------
    public string RequiredTimeZoneId { get; set; } = "Eastern Standard Time";
    public string HomeTimeZoneId { get; set; } = "Iran Standard Time";
    public TimeZoneMode TimeZoneMode { get; set; } = TimeZoneMode.Automatic;

    /// <summary>Use the elevated scheduled tasks so switching stops asking for UAC.</summary>
    public bool UsePrivilegedHelper { get; set; }

    // ---- detection ----------------------------------------------------
    public string VpnAdapterPattern { get; set; } = DefaultVpnPattern;
    public string ProcessNamePattern { get; set; } = DefaultProcessPattern;

    /// <summary>Adapters the user marked by hand as "this one is my VPN".</summary>
    public List<string> TrustedAdapters { get; set; } = new();

    /// <summary>Adapters that look like a VPN but must be ignored.</summary>
    public List<string> IgnoredAdapters { get; set; } = new();

    /// <summary>Extra process names to treat as Claude (exact, without .exe).</summary>
    public List<string> ExtraProcessNames { get; set; } = new();

    /// <summary>Process names that must never be stopped.</summary>
    public List<string> ExcludedProcessNames { get; set; } = new();

    /// <summary>Count tunnel/PPP adapters as a VPN even when the name does not match.</summary>
    public bool TrustTunnelAdapters { get; set; } = true;

    /// <summary>
    /// A tunnel only counts while it is the adapter your internet traffic actually
    /// leaves by. Mesh VPNs like Tailscale and ZeroTier sit up all day to reach
    /// other machines without moving your browsing anywhere, and an adapter like
    /// that looks exactly like protection while providing none. An adapter the
    /// user marked as trusted is counted either way.
    /// </summary>
    public bool RequireDefaultRoute { get; set; } = true;

    // ---- kill switch ---------------------------------------------------
    /// <summary>Block Claude's traffic at the firewall while a rule is broken.</summary>
    public bool EnableFirewallKillSwitch { get; set; }

    /// <summary>Which executable the firewall rule was built for, so a move is noticed.</summary>
    public string FirewallRulePath { get; set; } = string.Empty;

    // ---- address watch --------------------------------------------------
    public bool ShowIpPanel { get; set; } = true;
    public int IpRefreshSeconds { get; set; } = 60;

    /// <summary>The address seen with no tunnel up, used to spot a leak.</summary>
    public string KnownHomeIp { get; set; } = string.Empty;

    // ---- usage report ---------------------------------------------------
    /// <summary>An Anthropic Admin API key (sk-ant-admin...). Read-only reporting.</summary>
    public string AdminApiKey { get; set; } = string.Empty;

    public int UsageDays { get; set; } = 30;

    // ---- orders ---------------------------------------------------------
    public string OrdersBaseUrl { get; set; } = string.Empty;

    // ---- application --------------------------------------------------
    public bool StartWithWindows { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;
    public bool NotifyOnlyOnProblems { get; set; }
    public bool PlaySound { get; set; }
    public bool ConfirmBeforeStopping { get; set; }
    public string ClaudeExecutablePath { get; set; } = string.Empty;
    public bool EnableKillHotkey { get; set; }

    // ---- appearance ---------------------------------------------------
    public AppTheme Theme { get; set; } = AppTheme.Dark;
    public string AccentColor { get; set; } = "#D97757";
    public string Language { get; set; } = "en";

    // ---- housekeeping -------------------------------------------------
    public int LogRetentionDays { get; set; } = 30;
    public bool FirstRunCompleted { get; set; }

    [JsonIgnore]
    public TimeSpan Refresh => TimeSpan.FromSeconds(Math.Clamp(RefreshSeconds, 0.5, 60));

    public GuardSettings Clone()
    {
        return new GuardSettings
        {
            EnforceVpn = EnforceVpn,
            EnforceTimeZone = EnforceTimeZone,
            Action = Action,
            VpnMissTolerance = VpnMissTolerance,
            RefreshSeconds = RefreshSeconds,
            GraceSeconds = GraceSeconds,
            ShowGraceOverlay = ShowGraceOverlay,
            RequiredTimeZoneId = RequiredTimeZoneId,
            HomeTimeZoneId = HomeTimeZoneId,
            TimeZoneMode = TimeZoneMode,
            UsePrivilegedHelper = UsePrivilegedHelper,
            VpnAdapterPattern = VpnAdapterPattern,
            ProcessNamePattern = ProcessNamePattern,
            TrustedAdapters = new List<string>(TrustedAdapters),
            IgnoredAdapters = new List<string>(IgnoredAdapters),
            ExtraProcessNames = new List<string>(ExtraProcessNames),
            ExcludedProcessNames = new List<string>(ExcludedProcessNames),
            TrustTunnelAdapters = TrustTunnelAdapters,
            RequireDefaultRoute = RequireDefaultRoute,
            EnableFirewallKillSwitch = EnableFirewallKillSwitch,
            FirewallRulePath = FirewallRulePath,
            ShowIpPanel = ShowIpPanel,
            IpRefreshSeconds = IpRefreshSeconds,
            KnownHomeIp = KnownHomeIp,
            AdminApiKey = AdminApiKey,
            UsageDays = UsageDays,
            OrdersBaseUrl = OrdersBaseUrl,
            StartWithWindows = StartWithWindows,
            StartMinimized = StartMinimized,
            MinimizeToTray = MinimizeToTray,
            CloseToTray = CloseToTray,
            ShowNotifications = ShowNotifications,
            NotifyOnlyOnProblems = NotifyOnlyOnProblems,
            PlaySound = PlaySound,
            ConfirmBeforeStopping = ConfirmBeforeStopping,
            ClaudeExecutablePath = ClaudeExecutablePath,
            EnableKillHotkey = EnableKillHotkey,
            Theme = Theme,
            AccentColor = AccentColor,
            Language = Language,
            LogRetentionDays = LogRetentionDays,
            FirstRunCompleted = FirstRunCompleted
        };
    }
}
