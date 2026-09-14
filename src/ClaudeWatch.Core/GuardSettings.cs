using System.Linq;
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

    // ---- services -------------------------------------------------------

    /// <summary>Claude and ChatGPT, each with its own rules.</summary>
    public List<ServiceProfile> Services { get; set; } = new();

    /// <summary>Which one the window is showing and whose clock rule applies.</summary>
    public string ActiveServiceKey { get; set; } = ServiceProfile.ClaudeKey;

    [JsonIgnore]
    public ServiceProfile Active
    {
        get
        {
            EnsureServices();
            return Services.FirstOrDefault(s =>
                       string.Equals(s.Key, ActiveServiceKey, StringComparison.OrdinalIgnoreCase))
                   ?? Services[0];
        }
    }

    [JsonIgnore]
    public IEnumerable<ServiceProfile> Watched
    {
        get
        {
            EnsureServices();
            return Services.Where(s => s.Enabled);
        }
    }

    public ServiceProfile? ByKey(string? key)
    {
        EnsureServices();
        return Services.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A settings file with no services yet is still usable.</summary>
    public void EnsureServices()
    {
        if (Services.Count == 0)
        {
            Services = ServiceProfile.Defaults();
        }
    }

    // ---- time zones ---------------------------------------------------
    public TimeZoneMode TimeZoneMode { get; set; } = TimeZoneMode.Automatic;

    /// <summary>Use the elevated scheduled tasks so switching stops asking for UAC.</summary>
    public bool UsePrivilegedHelper { get; set; }

    // ---- detection ----------------------------------------------------
    public string VpnAdapterPattern { get; set; } = DefaultVpnPattern;

    /// <summary>Adapters the user marked by hand as "this one is my VPN".</summary>
    public List<string> TrustedAdapters { get; set; } = new();

    /// <summary>Adapters that look like a VPN but must be ignored.</summary>
    public List<string> IgnoredAdapters { get; set; } = new();

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

    // ---- address watch --------------------------------------------------
    public bool ShowIpPanel { get; set; } = true;
    public int IpRefreshSeconds { get; set; } = 60;

    /// <summary>The address seen with no tunnel up, used to spot a leak.</summary>
    public string KnownHomeIp { get; set; } = string.Empty;

    // ---- usage report ---------------------------------------------------
    /// <summary>An Anthropic Admin API key (sk-ant-admin...). Read-only reporting.</summary>


    // ---- orders ---------------------------------------------------------

    /// <summary>
    /// Where the app looks for prices, orders and keys. This is the service's
    /// own address, so a fresh install already points at it and nobody has to
    /// be told what to type. Anyone running their own server replaces it in
    /// Settings, and that choice is kept.
    ///
    /// The subdomain is deliberate: safechat.ir itself is the landing page
    /// people arrive at, and the service sits beside it rather than under it.
    /// </summary>
    public const string DefaultOrdersBaseUrl = "https://app.safechat.ir";

    public string OrdersBaseUrl { get; set; } = DefaultOrdersBaseUrl;

    // ---- application --------------------------------------------------
    public bool StartWithWindows { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;
    public bool NotifyOnlyOnProblems { get; set; }
    public bool PlaySound { get; set; }
    public bool ConfirmBeforeStopping { get; set; }
    public bool EnableKillHotkey { get; set; }

    // ---- appearance ---------------------------------------------------
    public AppTheme Theme { get; set; } = AppTheme.Dark;
    public string AccentColor { get; set; } = "#D97757";
    public string Language { get; set; } = "en";

    // ---- licence --------------------------------------------------------
    /// <summary>The key the customer activated, as they typed it.</summary>
    public string LicenceKey { get; set; } = string.Empty;

    // ---- housekeeping -------------------------------------------------
    public int LogRetentionDays { get; set; } = 30;
    public bool FirstRunCompleted { get; set; }

    /// <summary>The first-run questions have been answered.</summary>
    public bool SetupCompleted { get; set; }

    [JsonIgnore]
    public TimeSpan Refresh => TimeSpan.FromSeconds(Math.Clamp(RefreshSeconds, 0.5, 60));

    public GuardSettings Clone()
    {
        return new GuardSettings
        {
            EnforceVpn = EnforceVpn,
            Action = Action,
            VpnMissTolerance = VpnMissTolerance,
            RefreshSeconds = RefreshSeconds,
            GraceSeconds = GraceSeconds,
            ShowGraceOverlay = ShowGraceOverlay,
            Services = Services.Select(s => s.Clone()).ToList(),
            ActiveServiceKey = ActiveServiceKey,
            TimeZoneMode = TimeZoneMode,
            UsePrivilegedHelper = UsePrivilegedHelper,
            VpnAdapterPattern = VpnAdapterPattern,
            TrustedAdapters = new List<string>(TrustedAdapters),
            IgnoredAdapters = new List<string>(IgnoredAdapters),
            TrustTunnelAdapters = TrustTunnelAdapters,
            RequireDefaultRoute = RequireDefaultRoute,
            ShowIpPanel = ShowIpPanel,
            IpRefreshSeconds = IpRefreshSeconds,
            KnownHomeIp = KnownHomeIp,
            OrdersBaseUrl = OrdersBaseUrl,
            StartWithWindows = StartWithWindows,
            StartMinimized = StartMinimized,
            MinimizeToTray = MinimizeToTray,
            CloseToTray = CloseToTray,
            ShowNotifications = ShowNotifications,
            NotifyOnlyOnProblems = NotifyOnlyOnProblems,
            PlaySound = PlaySound,
            ConfirmBeforeStopping = ConfirmBeforeStopping,
            EnableKillHotkey = EnableKillHotkey,
            Theme = Theme,
            AccentColor = AccentColor,
            Language = Language,
            LicenceKey = LicenceKey,
            LogRetentionDays = LogRetentionDays,
            FirstRunCompleted = FirstRunCompleted,
            SetupCompleted = SetupCompleted
        };
    }
}
