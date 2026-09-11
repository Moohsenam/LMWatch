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
        Console.WriteLine("Claude Watch — rule tests");
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
        AdminKeysAreRecognised();
        TheSetupScriptIsWellFormed();
        GraceHoldsFireThenStops();
        GraceClearsWhenTheVpnReturns();
        GraceCanBeSwitchedOff();
        AnIdleMeshVpnIsNotProtection();
        TheDefaultPatternStillFindsTailscale();
        ThePriceListIsReadCorrectly();

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
        settings.EnforceTimeZone = false;

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
            ProcessNamePattern = "([unclosed",
            VpnAdapterPattern = string.Empty,
            AccentColor = string.Empty,
            Language = "kl",
            TrustedAdapters = new List<string> { " Wintun ", "wintun", string.Empty }
        };

        SettingsStore.Sanitize(settings);

        Check("Refresh is clamped", Math.Abs(settings.RefreshSeconds - 60) < 0.001);
        Check("Tolerance is clamped", settings.VpnMissTolerance == 1);
        Check("A broken process pattern is replaced", settings.ProcessNamePattern == GuardSettings.DefaultProcessPattern);
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
        settings.ExtraProcessNames.Add("cursor");
        settings.ExcludedProcessNames.Add("Claude Updater");

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

    private static void AdminKeysAreRecognised()
    {
        Check("an admin key is accepted", UsageClient.LooksLikeAdminKey("sk-ant-admin01-abc"));
        Check("a normal key is not", !UsageClient.LooksLikeAdminKey("sk-ant-api03-abc"));
        Check("blank is not", !UsageClient.LooksLikeAdminKey(""));
    }

    private static void TheSetupScriptIsWellFormed()
    {
        var settings = Defaults();
        var script = PrivilegedHelper.BuildSetupScript(
            settings,
            @"C:\Users\me\AppData\Local\AnthropicClaude\app-1.2.3\claude.exe",
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
            lines.Count(l => l.Contains("ClaudeWatch-SetWorkTimeZone") || l.Contains("ClaudeWatch-SetHomeTimeZone")) == 2);
        Check("both firewall switches are registered",
            lines.Count(l => l.Contains("ClaudeWatch-FirewallOn") || l.Contains("ClaudeWatch-FirewallOff")) == 2);
        Check("the block rule is created", lines.Any(l => l.Contains("advfirewall firewall add rule")));
        Check("the old rule is cleared first",
            lines.FindIndex(l => l.Contains("delete rule")) < lines.FindIndex(l => l.Contains("add rule")));
        Check("the rule starts switched off", lines.Any(l => l.Contains("enable=no") && l.Contains("add rule")));
        Check("every task runs with full rights",
            lines.Where(l => l.StartsWith("schtasks")).All(l => l.Contains("/rl highest")));
        Check("no task fires on its own",
            lines.Where(l => l.StartsWith("schtasks")).All(l => l.Contains("ONEVENT")));
        Check("the work zone is passed through", script.Contains(settings.RequiredTimeZoneId));
        Check("the home zone is passed through", script.Contains(settings.HomeTimeZoneId));
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
            settings, string.Empty, "tzutil.exe", "netsh.exe", withFirewall: false);

        Check("without Claude the firewall part is skipped", !noFirewall.Contains("advfirewall"));
        Check("without Claude the clock switches remain", noFirewall.Contains("ClaudeWatch-SetWorkTimeZone"));
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
