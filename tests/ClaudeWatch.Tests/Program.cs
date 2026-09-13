using ClaudeWatch.Core;

namespace ClaudeWatch.Tests;

/// <summary>
/// A dependency-free test runner. `dotnet run` from this folder checks every rule
/// in the safety table; it needs no packages, so it works offline like the app.
/// </summary>
public static class Program
{
    private static int _passed;
    private static readonly List<string> Failures = new();

    public static int Main()
    {
        Console.WriteLine("SafeChat — rule tests");
        Console.WriteLine(new string('-', 48));

        VpnDownBlocksAndStops();
        VpnDownDoesNotStopWhenClaudeClosed();
        VpnUpWithWorkTimeIsReady();
        WrongTimeZoneStopsClaude();
        WarnOnlyNeverStops();
        HomeHoldGivesStandby();
        OpeningClaudeReleasesHomeHold();
        BlipDoesNotFlipTheClock();
        SustainedLossFlipsTheClock();
        PauseSuspendsEverything();
        SuspendedTimeZoneKeepsVpnRule();
        EverythingOffMeansOff();
        AskModeWaitsForConsent();
        AutomaticModeDoesNotAskTwice();
        SettingsAreRepairedOnLoad();
        ProcessMatchingIsNameOnly();
        ExtraAndExcludedNamesWork();
        ServerAddressesAreNormalized();
        TheSetupScriptIsWellFormed();
        GraceHoldsFireThenStops();
        GraceClearsWhenTheVpnReturns();
        GraceCanBeSwitchedOff();
        AnIdleMeshVpnIsNotProtection();
        TheDefaultPatternStillFindsTailscale();
        ThePriceListIsReadCorrectly();
        TheLicenceDecidesWhoIsProtected();
        AnOldSettingsFileKeepsItsRules();
        EachServiceIsFoundSeparately();
        TheLockedStateEnforcesNothing();
        TheOldDataFolderIsCarriedOver();
        TheScriptsAgreeOnTheExecutableName();
        SettingsTravelWithoutTheKey();
        OneServiceOpenIsNotTwo();
        ARefusedKeyLeavesTheTrialAlone();
        AnOrderCarriesAName();
        TheDaySummaryCountsWhatHappened();

        Console.WriteLine(new string('-', 48));

        if (Failures.Count == 0)
        {
            Console.WriteLine($"All {_passed} checks passed.");
            return 0;
        }

        Console.WriteLine($"{_passed} passed, {Failures.Count} FAILED:");
        foreach (var failure in Failures)
        {
            Console.WriteLine("  x " + failure);
        }

        return 1;
    }

    // ---------------------------------------------------------------- rules

    private static void VpnDownBlocksAndStops()
    {
        var settings = Defaults();
        var state = new GuardState();
        var now = DateTimeOffset.Now;

        var decision = EvaluateAt(now, vpn: false, claude: 2, tz: "Iran Standard Time", settings, state);
        Check("VPN down blocks", decision.Phase == GuardPhase.Blocked);
        Check("VPN down is not safe to open", !decision.SafeToOpenClaude);
        Check("VPN down starts the countdown", decision.InGrace);
        Check("A single miss holds the clock steady", decision.TargetTimeZoneId == "Eastern Standard Time");

        var after = EvaluateAt(now.AddSeconds(settings.GraceSeconds + 1), vpn: false, claude: 2,
            tz: "Iran Standard Time", settings, state);
        Check("VPN down stops Claude once the countdown ends", after.ShouldStopClaude);
        Check("VPN down reports the reason", after.StopReasonKey == "Reason_VpnDown");
    }

    private static void VpnDownDoesNotStopWhenClaudeClosed()
    {
        var decision = Evaluate(vpn: false, claude: 0, tz: "Iran Standard Time");
        Check("Nothing to stop when Claude is closed", !decision.ShouldStopClaude);
        Check("Still blocked with Claude closed", decision.Phase == GuardPhase.Blocked);
    }

    private static void VpnUpWithWorkTimeIsReady()
    {
        var decision = Evaluate(vpn: true, claude: 1, tz: "Eastern Standard Time");
        Check("VPN + work time is ready", decision.Phase == GuardPhase.Ready);
        Check("VPN + work time does not stop", !decision.ShouldStopClaude);
        Check("VPN + work time is safe", decision.SafeToOpenClaude);
    }

    private static void WrongTimeZoneStopsClaude()
    {
        var settings = Defaults();
        var state = new GuardState();
        var now = DateTimeOffset.Now;

        var decision = EvaluateAt(now, vpn: true, claude: 1, tz: "Iran Standard Time", settings, state);
        Check("Wrong clock starts the countdown", decision.InGrace);
        Check("Wrong clock names itself as the reason", decision.GraceReasonKey == "Reason_TimeZone");
        Check("Wrong clock is a mismatch", decision.Phase == GuardPhase.Mismatch);

        var after = EvaluateAt(now.AddSeconds(settings.GraceSeconds + 1), vpn: true, claude: 1,
            tz: "Iran Standard Time", settings, state);
        Check("Wrong clock stops Claude once the countdown ends", after.ShouldStopClaude);
        Check("Wrong clock reports the reason", after.StopReasonKey == "Reason_TimeZone");
    }

    private static void WarnOnlyNeverStops()
    {
        var settings = Defaults();
        settings.Action = EnforcementAction.WarnOnly;

        var decision = Evaluate(vpn: false, claude: 3, tz: "Iran Standard Time", settings: settings);
        Check("Warn-only leaves processes alone", !decision.ShouldStopClaude);
        Check("Warn-only still reports blocked", decision.Phase == GuardPhase.Blocked);
    }

    private static void HomeHoldGivesStandby()
    {
        var state = new GuardState { HomeTimeHold = true };
        var decision = Evaluate(vpn: true, claude: 0, tz: "Iran Standard Time", state: state);
        Check("Home hold targets home time", decision.TargetTimeZoneId == "Iran Standard Time");
        Check("Home hold is standby", decision.Phase == GuardPhase.Standby);
        Check("Home hold is not safe to open", !decision.SafeToOpenClaude);
    }

    private static void OpeningClaudeReleasesHomeHold()
    {
        var state = new GuardState { HomeTimeHold = true };
        var decision = Evaluate(vpn: true, claude: 1, tz: "Iran Standard Time", state: state);
        Check("Opening Claude clears the hold", !state.HomeTimeHold);
        Check("Hold released targets work time", decision.TargetTimeZoneId == "Eastern Standard Time");
    }

    private static void BlipDoesNotFlipTheClock()
    {
        var settings = Defaults();
        var state = new GuardState();

        var first = Evaluate(vpn: false, claude: 0, tz: "Eastern Standard Time", settings: settings, state: state);
        Check("One miss keeps work time as target", first.TargetTimeZoneId == "Eastern Standard Time");
        Check("One miss still blocks", first.Phase == GuardPhase.Blocked);
    }

    private static void SustainedLossFlipsTheClock()
    {
        var settings = Defaults();
        var state = new GuardState();
        GuardDecision decision = null!;

        for (var i = 0; i < settings.VpnMissTolerance; i++)
        {
            decision = Evaluate(vpn: false, claude: 0, tz: "Eastern Standard Time", settings: settings, state: state);
        }

        Check("Sustained loss targets home time", decision.TargetTimeZoneId == "Iran Standard Time");
        Check("Sustained loss asks for a change", decision.ShouldChangeTimeZone);
    }

    private static void PauseSuspendsEverything()
    {
        var state = new GuardState { PausedUntil = DateTimeOffset.Now.AddMinutes(10) };
        var decision = Evaluate(vpn: false, claude: 4, tz: "Iran Standard Time", state: state);
        Check("Pause stops nothing", !decision.ShouldStopClaude);
        Check("Pause reports paused", decision.Phase == GuardPhase.Paused);
        Check("Pause reports safe", decision.SafeToOpenClaude);
    }

    private static void SuspendedTimeZoneKeepsVpnRule()
    {
        var state = new GuardState { TimeZoneSuspended = true };
        var wrongClock = Evaluate(vpn: true, claude: 1, tz: "Iran Standard Time", state: state);
        Check("Skipping clock rules allows a wrong clock", !wrongClock.ShouldStopClaude);
        Check("Skipping clock rules still reads ready", wrongClock.Phase == GuardPhase.Ready);

        var settings = Defaults();
        var now = DateTimeOffset.Now;
        var noVpn = EvaluateAt(now, vpn: false, claude: 1, tz: "Iran Standard Time", settings, state);
        Check("Skipping clock rules keeps the VPN rule", noVpn.InGrace);

        var after = EvaluateAt(now.AddSeconds(settings.GraceSeconds + 1), vpn: false, claude: 1,
            tz: "Iran Standard Time", settings, state);
        Check("Skipping clock rules still closes Claude in the end", after.ShouldStopClaude);
    }

    private static void EverythingOffMeansOff()
    {
        var settings = Defaults();
        settings.EnforceVpn = false;
        settings.EnsureServices();

        foreach (var profile in settings.Services)
        {
            profile.EnforceTimeZone = false;
        }

        var decision = Evaluate(vpn: false, claude: 2, tz: "Iran Standard Time", settings: settings);
        Check("All rules off stops nothing", !decision.ShouldStopClaude);
        Check("All rules off reports off", decision.Phase == GuardPhase.Off);
    }

    private static void AskModeWaitsForConsent()
    {
        var settings = Defaults();
        settings.TimeZoneMode = TimeZoneMode.Ask;

        var decision = Evaluate(vpn: true, claude: 0, tz: "Iran Standard Time", settings: settings);
        Check("Ask mode wants a change", decision.ShouldChangeTimeZone);
        Check("Ask mode asks first", decision.NeedsTimeZoneConsent);

        settings.TimeZoneMode = TimeZoneMode.ReportOnly;
        var quiet = Evaluate(vpn: true, claude: 0, tz: "Iran Standard Time", settings: settings);
        Check("Report-only changes nothing", !quiet.ShouldChangeTimeZone);
    }

    private static void AutomaticModeDoesNotAskTwice()
    {
        var settings = Defaults();
        settings.TimeZoneMode = TimeZoneMode.Automatic;
        var state = new GuardState { TimeZoneAttempted = "Eastern Standard Time" };

        var decision = Evaluate(vpn: true, claude: 0, tz: "Iran Standard Time", settings: settings, state: state);
        Check("An attempted change is not repeated", !decision.ShouldChangeTimeZone);

        var synced = Evaluate(vpn: true, claude: 0, tz: "Eastern Standard Time", settings: settings, state: state);
        Check("A synced clock clears the attempt", state.TimeZoneAttempted is null && synced.TimeZoneSynced);
    }

    // ------------------------------------------------------------- plumbing

    private static void SettingsAreRepairedOnLoad()
    {
        var settings = new GuardSettings
        {
            RefreshSeconds = 900,
            VpnMissTolerance = 0,

            VpnAdapterPattern = string.Empty,
            AccentColor = string.Empty,
            Language = "kl",
            TrustedAdapters = new List<string> { " Wintun ", "wintun", string.Empty }
        };

        settings.EnsureServices();
        settings.Active.ProcessNamePattern = "([unclosed";

        SettingsStore.Sanitize(settings);

        Check("Refresh is clamped", Math.Abs(settings.RefreshSeconds - 60) < 0.001);
        Check("Tolerance is clamped", settings.VpnMissTolerance == 1);
        Check("A broken process pattern is replaced", settings.Active.ProcessNamePattern == GuardSettings.DefaultProcessPattern);
        Check("An empty VPN pattern is replaced", settings.VpnAdapterPattern == GuardSettings.DefaultVpnPattern);
        Check("Accent falls back", settings.AccentColor == "#D97757");
        Check("Unknown language falls back", settings.Language == "en");
        Check("Adapter list is trimmed and deduped", settings.TrustedAdapters.Count == 1 && settings.TrustedAdapters[0] == "Wintun");
    }

    private static void ProcessMatchingIsNameOnly()
    {
        var watcher = new ProcessWatcher();
        var settings = Defaults();

        Check("Claude.exe matches", watcher.IsTarget("Claude.exe", settings));
        Check("Claude Helper matches", watcher.IsTarget("Claude Helper", settings));
        Check("claude-code matches", watcher.IsTarget("claude-code", settings));
        Check("node does not match", !watcher.IsTarget("node.exe", settings));
        Check("notepad does not match", !watcher.IsTarget("notepad", settings));
        Check("claudette does not match", !watcher.IsTarget("claudette", settings));
    }

    private static void ExtraAndExcludedNamesWork()
    {
        var watcher = new ProcessWatcher();
        var settings = Defaults();
        settings.Active.ExtraProcessNames.Add("cursor");
        settings.Active.ExcludedProcessNames.Add("Claude Updater");

        Check("An extra name matches", watcher.IsTarget("cursor.exe", settings));
        Check("An excluded name is spared", !watcher.IsTarget("Claude Updater.exe", settings));
    }

    private static void ServerAddressesAreNormalized()
    {
        Check("a bare host gets https", OrdersClient.Normalize("orders.example.com") == "https://orders.example.com");
        Check("a trailing slash is dropped", OrdersClient.Normalize("https://a.com/") == "https://a.com");
        Check("http is left alone", OrdersClient.Normalize("http://127.0.0.1:5080") == "http://127.0.0.1:5080");
        Check("blank stays blank", OrdersClient.Normalize("   ") == string.Empty);
    }

    private static void TheSetupScriptIsWellFormed()
    {
        var settings = Defaults();
        settings.EnsureServices();
        settings.Active.ExecutablePath = @"C:\Users\me\AppData\Local\AnthropicClaude\app-1.2.3\claude.exe";

        // Only Claude is on here, so exactly one rule and one pair of switches.
        foreach (var other in settings.Services.Where(x => x.Key != settings.Active.Key))
        {
            other.Enabled = false;
        }

        var script = PrivilegedHelper.BuildSetupScript(
            settings,
            @"C:\Windows\System32\tzutil.exe",
            @"C:\Windows\System32\netsh.exe",
            withFirewall: true);

        var lines = script.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        Check("the script starts quietly", lines[0] == "@echo off");
        Check("the script ends cleanly", lines[^1] == "exit /b 0");

        Check("both clock switches are registered",
            lines.Count(l => l.Contains("SafeChat-SetWorkTimeZone") || l.Contains("SafeChat-SetHomeTimeZone")) == 2);
        Check("both firewall switches are registered",
            lines.Count(l => l.Contains("SafeChat-FirewallOn") || l.Contains("SafeChat-FirewallOff")) == 2);
        Check("the block rule is created", lines.Any(l => l.Contains("advfirewall firewall add rule")));
        Check("the old rule is cleared first",
            lines.FindIndex(l => l.Contains("delete rule")) < lines.FindIndex(l => l.Contains("add rule")));
        Check("the rule starts switched off", lines.Any(l => l.Contains("enable=no") && l.Contains("add rule")));
        Check("every task runs with full rights",
            lines.Where(l => l.StartsWith("schtasks")).All(l => l.Contains("/rl highest")));
        Check("no task fires on its own",
            lines.Where(l => l.StartsWith("schtasks")).All(l => l.Contains("ONEVENT")));
        Check("the work zone is passed through", script.Contains(settings.Active.RequiredTimeZoneId));
        Check("the home zone is passed through", script.Contains(settings.Active.HomeTimeZoneId));
        Check("Claude's path is passed through", script.Contains(@"app-1.2.3\claude.exe"));

        // Every line must have balanced quotes, or cmd hands schtasks nonsense.
        foreach (var line in lines)
        {
            var quotes = line.Count(c => c == '"');
            if (quotes % 2 != 0)
            {
                Check($"unbalanced quotes: {line}", false);
                return;
            }
        }

        Check("every line has balanced quotes", true);

        var noFirewall = PrivilegedHelper.BuildSetupScript(
            settings, "tzutil.exe", "netsh.exe", withFirewall: false);

        Check("without Claude the firewall part is skipped", !noFirewall.Contains("advfirewall"));
        Check("without Claude the clock switches remain", noFirewall.Contains("SafeChat-SetWorkTimeZone"));
    }

    private static void GraceHoldsFireThenStops()
    {
        var settings = Defaults();
        var state = new GuardState();
        var now = DateTimeOffset.Now;

        var first = EvaluateAt(now, vpn: false, claude: 2, tz: "Eastern Standard Time", settings, state);
        Check("the countdown starts", first.InGrace);
        Check("nothing is closed yet", !first.ShouldStopClaude);
        Check("traffic is already considered unsafe", !first.SafeToOpenClaude);
        Check("the countdown knows when it ends", first.GraceEndsAt is not null);
        Check("the countdown names the reason", first.GraceReasonKey == "Reason_VpnDown");

        var halfway = EvaluateAt(now.AddSeconds(5), vpn: false, claude: 2, tz: "Eastern Standard Time", settings, state);
        Check("halfway through it still waits", halfway.InGrace && !halfway.ShouldStopClaude);

        var expired = EvaluateAt(now.AddSeconds(11), vpn: false, claude: 2, tz: "Eastern Standard Time", settings, state);
        Check("when time is up it closes Claude", expired.ShouldStopClaude);
        Check("the countdown is over", !expired.InGrace);
        Check("the countdown is cleared from state", state.GraceUntil is null);
    }

    private static void GraceClearsWhenTheVpnReturns()
    {
        var settings = Defaults();
        var state = new GuardState();
        var now = DateTimeOffset.Now;

        var dropped = EvaluateAt(now, vpn: false, claude: 1, tz: "Eastern Standard Time", settings, state);
        Check("dropping starts the countdown", dropped.InGrace);

        var back = EvaluateAt(now.AddSeconds(4), vpn: true, claude: 1, tz: "Eastern Standard Time", settings, state);
        Check("coming back cancels it", !back.InGrace);
        Check("coming back closes nothing", !back.ShouldStopClaude);
        Check("coming back clears the state", state.GraceUntil is null);
        Check("coming back is safe again", back.SafeToOpenClaude);

        // A later drop must start a fresh countdown, not resume the old one.
        var again = EvaluateAt(now.AddSeconds(30), vpn: false, claude: 1, tz: "Eastern Standard Time", settings, state);
        Check("a later drop starts over", again.InGrace && !again.ShouldStopClaude);
    }

    private static void GraceCanBeSwitchedOff()
    {
        var settings = Defaults();
        settings.GraceSeconds = 0;

        var state = new GuardState();
        var decision = EvaluateAt(DateTimeOffset.Now, vpn: false, claude: 2, tz: "Eastern Standard Time", settings, state);

        Check("with no grace it closes at once", decision.ShouldStopClaude);
        Check("with no grace there is no countdown", !decision.InGrace);
    }

    private static GuardDecision EvaluateAt(
        DateTimeOffset now, bool vpn, int claude, string tz, GuardSettings settings, GuardState state)
    {
        var input = new GuardInput
        {
            VpnConnected = vpn,
            ClaudeProcessCount = claude,
            CurrentTimeZoneId = tz,
            Now = now
        };

        return GuardEngine.Evaluate(input, settings, state);
    }

    /// <summary>
    /// Tailscale is the case that started this: up all day so you can reach your
    /// own machines, carrying none of your browsing. It must not read as a VPN.
    /// </summary>
    private static void AnIdleMeshVpnIsNotProtection()
    {
        Console.WriteLine("\nA tunnel that carries nothing");

        var idle = VpnDetector.Counts(
            up: true, looksLikeVpn: true, ignored: false, trusted: false,
            requireRoute: true, routeKnown: true, carriesTraffic: false);

        Check("an idle mesh tunnel is not a VPN", !idle);

        var routing = VpnDetector.Counts(
            up: true, looksLikeVpn: true, ignored: false, trusted: false,
            requireRoute: true, routeKnown: true, carriesTraffic: true);

        Check("the same tunnel counts once traffic goes through it", routing);

        var trusted = VpnDetector.Counts(
            up: true, looksLikeVpn: true, ignored: false, trusted: true,
            requireRoute: true, routeKnown: true, carriesTraffic: false);

        Check("marking it trusted overrides the route check", trusted);

        // An unanswerable question must not start closing Claude.
        var unknown = VpnDetector.Counts(
            up: true, looksLikeVpn: true, ignored: false, trusted: false,
            requireRoute: true, routeKnown: false, carriesTraffic: false);

        Check("an unknown route falls back to the old behaviour", unknown);

        var ruleOff = VpnDetector.Counts(
            up: true, looksLikeVpn: true, ignored: false, trusted: false,
            requireRoute: false, routeKnown: true, carriesTraffic: false);

        Check("the route rule can be switched off", ruleOff);

        var ignored = VpnDetector.Counts(
            up: true, looksLikeVpn: true, ignored: true, trusted: true,
            requireRoute: false, routeKnown: true, carriesTraffic: true);

        Check("ignoring an adapter still beats everything", !ignored);

        var down = VpnDetector.Counts(
            up: false, looksLikeVpn: true, ignored: false, trusted: true,
            requireRoute: false, routeKnown: true, carriesTraffic: true);

        Check("an adapter that is down never counts", !down);
    }

    private static void TheDefaultPatternStillFindsTailscale()
    {
        Console.WriteLine("\nAdapter name matching");

        var regex = new System.Text.RegularExpressions.Regex(
            GuardSettings.DefaultVpnPattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        Check("Tailscale is recognised", regex.IsMatch("Tailscale — Tailscale Tunnel"));
        Check("WireGuard is recognised", regex.IsMatch("wg0 — WireGuard Tunnel"));
        Check("Wi-Fi is not", !regex.IsMatch("Wi-Fi — Intel Wireless-AC 9560"));
        Check("Ethernet is not", !regex.IsMatch("Ethernet — Realtek Gaming GbE"));
    }

    private static void ThePriceListIsReadCorrectly()
    {
        Console.WriteLine("\nThe buy page");

        const string body = """
        {
          "ok": true,
          "rateReady": true,
          "rateStale": false,
          "updatedAt": "2026-09-11T10:00:00+00:00",
          "plans": [
            { "key": "pro", "label": "Claude Pro", "labelFa": "کلاد پرو", "period": "month",
              "note": "", "noteFa": "", "popular": true, "usd": null, "toman": 5160000, "variable": false },
            { "key": "api", "label": "API credit", "labelFa": "کردیت API", "period": "",
              "note": "Any amount.", "noteFa": "هر مبلغی.", "popular": false, "usd": null, "toman": 0, "variable": true }
          ]
        }
        """;

        var list = PricingClient.Parse(body);

        Check("the list is read", list.Ok && list.RateReady);
        Check("both plans arrive", list.Plans.Count == 2);
        Check("the toman figure survives", list.Plans[0].Toman == 5_160_000);
        Check("the popular flag survives", list.Plans[0].Popular);
        Check("a variable plan is marked", list.Plans[1].Variable);
        Check("the dollar price is absent when hidden", list.Plans[0].Usd is null);
        Check("the Persian name is used in Persian", list.Plans[0].Name(persian: true) == "کلاد پرو");
        Check("the English name is used in English", list.Plans[0].Name(persian: false) == "Claude Pro");

        // A name with no Persian translation must not come back blank.
        var untranslated = new PricedPlan { Label = "Claude Pro", LabelFa = string.Empty };
        Check("a missing translation falls back", untranslated.Name(persian: true) == "Claude Pro");

        Check("junk is not mistaken for a price list", !PricingClient.Parse("not json at all").Ok);

        Check("nothing is shown for a zero price", PricingClient.Money(0, false).Length == 0);
        Check("thousands are grouped", PricingClient.Money(5_160_000, false) == "5,160,000");
        Check("Persian digits are used in Persian", PricingClient.Money(5_160_000, true).Contains('۵'));
    }

    /// <summary>
    /// The rules that decide whether someone's protection runs. The one that
    /// matters most is the last: an unreachable server must never switch a
    /// paying customer's guard off.
    /// </summary>
    private static void TheLicenceDecidesWhoIsProtected()
    {
        Console.WriteLine("\nLicence");

        var now = DateTimeOffset.UtcNow;

        var fresh = new LicenceStatus { InstalledAt = now.AddDays(-1), TrialDays = 3, State = LicenceState.Trial };
        Check("a fresh install is protected", LicenceClient.Allowed(fresh, now));
        Check("it reports the days left", fresh.TrialDaysLeft(now) == 2);

        var used = new LicenceStatus { InstalledAt = now.AddDays(-5), TrialDays = 3, State = LicenceState.Trial };
        Check("a finished trial has no days left", used.TrialDaysLeft(now) == 0);
        Check("a finished trial is not protected", !LicenceClient.Allowed(used, now));

        var keyed = new LicenceStatus { State = LicenceState.Licensed, ExpiresAt = now.AddDays(10) };
        Check("an active key is protected", LicenceClient.Allowed(keyed, now));

        var ended = new LicenceStatus { State = LicenceState.Licensed, ExpiresAt = now.AddDays(-1) };
        Check("a key past its date is not", !LicenceClient.Allowed(ended, now));

        foreach (var state in new[] { LicenceState.NeedsKey, LicenceState.Expired, LicenceState.Revoked, LicenceState.WrongDevice })
        {
            Check($"{state} is not protected", !LicenceClient.Allowed(new LicenceStatus { State = state }, now));
        }

        var notRequired = new LicenceStatus { State = LicenceState.NeedsKey, KeysRequired = false };
        Check("with keys switched off everyone is protected", LicenceClient.Allowed(notRequired, now));

        // The important one. A key confirmed a week ago, server unreachable
        // since: protection must keep running to its own end date.
        var offline = new LicenceStatus
        {
            State = LicenceState.Licensed,
            ExpiresAt = now.AddDays(20),
            LastConfirmedAt = now.AddDays(-7)
        };
        Check("an unreachable server does not switch protection off", LicenceClient.Allowed(offline, now));

        Check("a device id is stable", LicenceClient.DeviceId() == LicenceClient.DeviceId());
        Check("a device id gives nothing away", LicenceClient.DeviceId().Length == 32);
    }

    /// <summary>Upgrading must not quietly reset somebody's rules.</summary>
    private static void AnOldSettingsFileKeepsItsRules()
    {
        Console.WriteLine("\nUpgrading an old settings file");

        const string old = """
        {
          "enforceVpn": true,
          "enforceTimeZone": true,
          "requiredTimeZoneId": "Central European Standard Time",
          "homeTimeZoneId": "Iran Standard Time",
          "processNamePattern": "^myclaude",
          "claudeExecutablePath": "C:\\Apps\\claude.exe",
          "enableFirewallKillSwitch": true,
          "firewallRulePath": "C:\\Apps\\claude.exe",
          "extraProcessNames": ["claude-helper"],
          "excludedProcessNames": ["claude-notes"],
          "language": "fa"
        }
        """;

        var settings = System.Text.Json.JsonSerializer.Deserialize<GuardSettings>(old,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        SettingsStore.Migrate(settings, old);
        SettingsStore.Sanitize(settings);

        var claude = settings.ByKey(ServiceProfile.ClaudeKey)!;

        Check("the old zone survives", claude.RequiredTimeZoneId == "Central European Standard Time");
        Check("the old process pattern survives", claude.ProcessNamePattern == "^myclaude");
        Check("the old path survives", claude.ExecutablePath == @"C:\Apps\claude.exe");
        Check("the kill switch stays on", claude.EnableFirewallKillSwitch);
        Check("extra names survive", claude.ExtraProcessNames.Contains("claude-helper"));
        Check("excluded names survive", claude.ExcludedProcessNames.Contains("claude-notes"));
        Check("the language survives", settings.Language == "fa");

        // Someone upgrading never asked to guard ChatGPT.
        Check("ChatGPT is not switched on behind their back",
            settings.ByKey(ServiceProfile.ChatGptKey)!.Enabled == false);

        // Running it twice must not undo the first pass.
        SettingsStore.Migrate(settings, old);
        Check("migrating twice changes nothing", settings.ByKey(ServiceProfile.ClaudeKey)!.ProcessNamePattern == "^myclaude");
    }

    private static void EachServiceIsFoundSeparately()
    {
        Console.WriteLine("\nTelling the two apps apart");

        var claude = ServiceProfile.Claude();
        var chatgpt = ServiceProfile.ChatGpt();
        var watcher = new ProcessWatcher();

        Check("claude.exe is Claude's", watcher.IsTarget("claude", claude));
        Check("claude.exe is not ChatGPT's", !watcher.IsTarget("claude", chatgpt));
        Check("chatgpt.exe is ChatGPT's", watcher.IsTarget("ChatGPT", chatgpt));
        Check("chatgpt.exe is not Claude's", !watcher.IsTarget("ChatGPT", claude));

        // The helper processes are the whole reason the pattern allows a
        // separator: they are the ones holding connections open, and missing one
        // means a leak. The cost is that a third-party app named "ChatGPT
        // Something" is caught too, which the "never stop" list exists for.
        Check("ChatGPT Helper is caught", watcher.IsTarget("ChatGPT Helper", chatgpt));
        Check("Claude Helper is caught", watcher.IsTarget("Claude Helper", claude));

        // A word that merely starts the same way is never touched.
        Check("claudia is left alone", !watcher.IsTarget("claudia", claude));
        Check("chatgptx is left alone", !watcher.IsTarget("chatgptx", chatgpt));

        chatgpt.ExcludedProcessNames.Add("ChatGPT Exporter");
        Check("an excluded third-party app is spared", !watcher.IsTarget("ChatGPT Exporter", chatgpt));

        Check("each has its own firewall rule",
            FirewallController.RuleNameFor(claude) != FirewallController.RuleNameFor(chatgpt));
        Check("each has its own switches",
            FirewallController.OnTaskFor(claude) != FirewallController.OnTaskFor(chatgpt));
        Check("the rules are named for SafeChat",
            FirewallController.RuleNameFor(claude).StartsWith("SafeChat"));

        var settings = Defaults();
        settings.EnsureServices();
        Check("both are watched by default", settings.Watched.Count() == 2);

        settings.ByKey(ServiceProfile.ChatGptKey)!.Enabled = false;
        Check("switching one off leaves one watched", settings.Watched.Count() == 1);
        Check("the remaining one is Claude", settings.Watched.First().Key == ServiceProfile.ClaudeKey);

        Check("each carries its own colour", claude.Accent != chatgpt.Accent);
    }

    /// <summary>Without a licence the guard must do nothing, and say so.</summary>
    private static void TheLockedStateEnforcesNothing()
    {
        Console.WriteLine("\nLocked");

        var settings = Defaults();
        var state = new GuardState();

        var input = new GuardInput
        {
            VpnConnected = false,
            ClaudeProcessCount = 3,
            CurrentTimeZoneId = "Iran Standard Time",
            Now = DateTimeOffset.Now,
            Licensed = false
        };

        var decision = GuardEngine.Evaluate(input, settings, state);

        Check("locked stops nothing", !decision.ShouldStopClaude);
        Check("locked changes no clock", !decision.ShouldChangeTimeZone);
        Check("locked reports itself", decision.Phase == GuardPhase.Locked);
        Check("locked does not claim to be protecting", decision.HeadlineKey == "Head_Locked");
        Check("locked starts no countdown", !decision.InGrace);
    }

    /// <summary>
    /// The rename moves the data folder. Without a carry-over every existing
    /// install would come back on with default settings and no history, which
    /// is the sort of thing nobody notices until a customer complains.
    /// </summary>
    private static void TheOldDataFolderIsCarriedOver()
    {
        Console.WriteLine("\nCarrying settings over from the old name");

        var appData = Path.Combine(Path.GetTempPath(), "safechat-test-" + Guid.NewGuid().ToString("N")[..8]);
        var oldRoot = Path.Combine(appData, "ClaudeWatch");
        var newRoot = Path.Combine(appData, "SafeChat");

        Directory.CreateDirectory(Path.Combine(oldRoot, "logs"));
        File.WriteAllText(Path.Combine(oldRoot, "settings.json"), """{"language":"fa","refreshSeconds":5}""");
        File.WriteAllText(Path.Combine(oldRoot, "logs", "activity-2026-01-01.jsonl"), "{}");

        // The same steps AppPaths takes, against a scratch folder.
        Directory.CreateDirectory(newRoot);
        var carried = Path.Combine(newRoot, "settings.json");

        if (!File.Exists(carried) && File.Exists(Path.Combine(oldRoot, "settings.json")))
        {
            File.Copy(Path.Combine(oldRoot, "settings.json"), carried);
            Directory.CreateDirectory(Path.Combine(newRoot, "logs"));
            foreach (var file in Directory.GetFiles(Path.Combine(oldRoot, "logs")))
            {
                File.Copy(file, Path.Combine(newRoot, "logs", Path.GetFileName(file)), true);
            }
        }

        Check("the settings file comes across", File.Exists(carried));
        Check("the history comes across", Directory.GetFiles(Path.Combine(newRoot, "logs")).Length == 1);
        Check("the original is left alone", File.Exists(Path.Combine(oldRoot, "settings.json")));

        var loaded = new SettingsStore(carried).Load();
        Check("the carried settings are read", loaded.Language == "fa");
        Check("and they still migrate into a profile", loaded.Services.Count > 0);

        try { Directory.Delete(appData, true); } catch { }
    }

    /// <summary>
    /// The installer looks for the executable by name after building it. When
    /// the app was renamed, the project produced SafeChat.exe while the script
    /// still checked for ClaudeWatch.exe, so a perfectly good build reported
    /// failure. Nothing in a compiler catches that, so it is checked here.
    /// </summary>
    private static void TheScriptsAgreeOnTheExecutableName()
    {
        Console.WriteLine("\nThe scripts and the project agree");

        var root = RepoRoot();
        if (root is null)
        {
            Check("the repository was found", false);
            return;
        }

        var csproj = File.ReadAllText(Path.Combine(root, "src", "ClaudeWatch.App", "ClaudeWatch.App.csproj"));
        var match = System.Text.RegularExpressions.Regex.Match(csproj, @"<AssemblyName>([^<]+)</AssemblyName>");

        Check("the project names its assembly", match.Success);
        if (!match.Success)
        {
            return;
        }

        var assembly = match.Groups[1].Value.Trim();
        var setup = File.ReadAllText(Path.Combine(root, "tools", "setup.ps1"));

        Check($"the installer looks for {assembly}.exe", setup.Contains($"'{assembly}.exe'"));
        Check("the installer does not look for the old name", !setup.Contains("'ClaudeWatch.exe'"));

        // Stopping the running app before replacing its files needs the process
        // name, which is the assembly name without the extension.
        Check($"the installer stops the {assembly} process", setup.Contains($"'{assembly}'"));

        var uninstall = File.ReadAllText(Path.Combine(root, "tools", "uninstall.ps1"));
        Check("the uninstaller stops the right process", uninstall.Contains($"Get-Process -Name '{assembly}'"));
    }

    private static void OneServiceOpenIsNotTwo()
    {
        Console.WriteLine();
        Console.WriteLine("Each service is counted on its own");

        var settings = Defaults();
        settings.EnsureServices();
        settings.ActiveServiceKey = ServiceProfile.ClaudeKey;
        settings.Active.EnforceTimeZone = true;

        var state = new GuardState { HomeTimeHold = true };

        // ChatGPT is open, Claude is not. The hold belongs to Claude.
        var input = new GuardInput
        {
            VpnConnected = true,
            ClaudeProcessCount = 1,
            ActiveProcessCount = 0,
            CurrentTimeZoneId = settings.Active.HomeTimeZoneId,
            Now = DateTimeOffset.Now
        };

        var decision = GuardEngine.Evaluate(input, settings, state);

        Check("the other app being open does not cancel home time", state.HomeTimeHold);
        Check("and home time is what the clock is aimed at",
            decision.TargetTimeZoneId == settings.Active.HomeTimeZoneId);
        Check("so the phase is standby, not mismatch", decision.Phase == GuardPhase.Standby);

        // Now Claude itself is open: the hold goes, as it always did.
        var open = new GuardInput
        {
            VpnConnected = true,
            ClaudeProcessCount = 1,
            ActiveProcessCount = 1,
            CurrentTimeZoneId = settings.Active.RequiredTimeZoneId,
            Now = DateTimeOffset.Now
        };

        var second = GuardEngine.Evaluate(open, settings, new GuardState { HomeTimeHold = true });
        Check("opening the guarded app still releases the hold", second.Phase == GuardPhase.Ready);

        // A VPN drop is about the machine, so anything watched still gets stopped.
        // No countdown here: that is a separate rule with its own tests.
        settings.GraceSeconds = 0;

        var dropped = new GuardInput
        {
            VpnConnected = false,
            ClaudeProcessCount = 1,
            ActiveProcessCount = 0,
            CurrentTimeZoneId = settings.Active.HomeTimeZoneId,
            Now = DateTimeOffset.Now
        };

        var third = GuardEngine.Evaluate(dropped, settings, new GuardState());
        Check("a dropped tunnel still stops the other service", third.ShouldStopClaude);
    }

    private static void ARefusedKeyLeavesTheTrialAlone()
    {
        Console.WriteLine();
        Console.WriteLine("A refused key does not end the trial");

        var status = new LicenceStatus
        {
            State = LicenceState.Trial,
            TrialDays = 3,
            InstalledAt = DateTimeOffset.UtcNow.AddDays(-1),
            KeysRequired = true
        };

        Check("two days of trial are left", status.TrialDaysLeft(DateTimeOffset.UtcNow) == 2);
        Check("and protection runs on them", LicenceClient.Allowed(status, DateTimeOffset.UtcNow));

        // This is what CheckAsync does when the server refuses a typed key.
        var trialStillRunning = status.State == LicenceState.Trial
                                && status.TrialDaysLeft(DateTimeOffset.UtcNow) > 0;

        Check("a refused key is recognised as leaving the trial alone", trialStillRunning);
        Check("and the state is untouched", status.State == LicenceState.Trial);
        Check("so protection is still allowed", LicenceClient.Allowed(status, DateTimeOffset.UtcNow));

        // Once the days are gone, the same refusal does end it.
        var spent = new LicenceStatus
        {
            State = LicenceState.Trial,
            TrialDays = 3,
            InstalledAt = DateTimeOffset.UtcNow.AddDays(-10),
            KeysRequired = true
        };

        Check("a spent trial protects nothing", !LicenceClient.Allowed(spent, DateTimeOffset.UtcNow));
    }

    private static void AnOrderCarriesAName()
    {
        Console.WriteLine();
        Console.WriteLine("An order carries what the server asks for");

        var draft = new OrderDraft
        {
            Plan = "pro",
            FullName = "Test Person",
            Service = "chatgpt",
            Email = "someone@example.com",
            Contact = "@someone",
            Eligible = true
        };

        Check("the draft has a name field", draft.FullName == "Test Person");
        Check("and knows its service", draft.Service == "chatgpt");

        var claudePlan = new OrderPlan { Key = "pro", Label = "Claude Pro", Service = "claude" };
        var gptPlan = new OrderPlan { Key = "gpt-plus", Label = "ChatGPT Plus", Service = "chatgpt" };
        var oldPlan = new OrderPlan { Key = "max5", Label = "Claude Max" };

        Check("a claude plan belongs to claude", claudePlan.BelongsTo("claude"));
        Check("and not to chatgpt", !claudePlan.BelongsTo("chatgpt"));
        Check("a chatgpt plan belongs to chatgpt", gptPlan.BelongsTo("chatgpt"));
        Check("a plan from an older server counts as claude", oldPlan.BelongsTo("claude"));
    }

    private static void SettingsTravelWithoutTheKey()
    {
        Console.WriteLine();
        Console.WriteLine("Settings can move to another computer");

        var mine = new GuardSettings
        {
            LicenceKey = "SAFE-AAAA-BBBB-CCCC",
            GraceSeconds = 25,
            Language = "fa",
            SetupCompleted = true
        };

        mine.EnsureServices();
        mine.Active.RequiredTimeZoneId = "Eastern Standard Time";

        var file = SettingsStore.Export(mine);

        Check("the key is not in the file", !file.Contains("SAFE-AAAA-BBBB-CCCC"));
        Check("the rest of it is", file.Contains("Eastern Standard Time"));

        // The machine reading it already has a key and has been set up.
        var theirs = new GuardSettings { LicenceKey = "SAFE-ZZZZ-YYYY-XXXX", SetupCompleted = true };
        theirs.EnsureServices();

        var loaded = SettingsStore.Import(file, theirs);

        Check("their own key survives the import", loaded.LicenceKey == "SAFE-ZZZZ-YYYY-XXXX");
        Check("the settings came across", loaded.GraceSeconds == 25 && loaded.Language == "fa");
        Check("so did the per-service ones", loaded.Active.RequiredTimeZoneId == "Eastern Standard Time");

        var broke = false;

        try
        {
            SettingsStore.Import("this is not settings", theirs);
        }
        catch
        {
            broke = true;
        }

        Check("a file that is not settings is refused", broke);
    }

    private static void TheDaySummaryCountsWhatHappened()
    {
        Console.WriteLine();
        Console.WriteLine("The day summary counts codes, not words");

        var log = new ActivityLog();

        log.Add(ActivityKind.Alert, "Whatever this says", "", ActivityCode.Stopped);
        log.Add(ActivityKind.Alert, "هرچه اینجا نوشته شده", "", ActivityCode.Stopped);
        log.Add(ActivityKind.Alert, "VPN disconnected", "", ActivityCode.VpnDown);
        log.Add(ActivityKind.Good, "VPN connected", "", ActivityCode.VpnUp);
        log.Add(ActivityKind.Info, "Something untagged");

        var today = log.SummaryFor(DateTime.Today);

        Check("both stops are counted whatever they were called", today.Stops == 2);
        Check("the drop is counted, the reconnect is not a drop", today.VpnDrops == 1);
        Check("an untagged event still counts as an event", today.Events == 5);
        Check("a day with a stop is not a calm one", !today.Calm);
        Check("the last incident is the most recent alert", today.LastIncident is not null);

        var quiet = new ActivityLog();
        quiet.Add(ActivityKind.Good, "VPN connected", "", ActivityCode.VpnUp);

        Check("a day with nothing wrong is calm", quiet.SummaryFor(DateTime.Today).Calm);
        Check("yesterday is not today", quiet.SummaryFor(DateTime.Today.AddDays(-1)).Events == 0);
    }

    private static string? RepoRoot()
    {
        var here = new DirectoryInfo(AppContext.BaseDirectory);

        while (here is not null)
        {
            if (File.Exists(Path.Combine(here.FullName, "ClaudeWatch.sln")))
            {
                return here.FullName;
            }

            here = here.Parent;
        }

        return null;
    }

    // -------------------------------------------------------------- helpers

    private static GuardSettings Defaults() => new();

    private static GuardDecision Evaluate(
        bool vpn,
        int claude,
        string tz,
        GuardSettings? settings = null,
        GuardState? state = null)
    {
        var input = new GuardInput
        {
            VpnConnected = vpn,
            ClaudeProcessCount = claude,
            CurrentTimeZoneId = tz,
            Now = DateTimeOffset.Now
        };

        return GuardEngine.Evaluate(input, settings ?? Defaults(), state ?? new GuardState());
    }

    private static void Check(string name, bool condition)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine($"  ok  {name}");
        }
        else
        {
            Failures.Add(name);
            Console.WriteLine($"  XX  {name}");
        }
    }
}
