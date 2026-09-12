namespace ClaudeWatch.Core;

/// <summary>Overall state of the machine, one of six.</summary>
public enum GuardPhase
{
    /// <summary>VPN confirmed and the clock matches. Claude may run.</summary>
    Ready,

    /// <summary>A rule is broken. Claude is stopped and kept stopped.</summary>
    Blocked,

    /// <summary>Home time restored on purpose while Claude is closed.</summary>
    Standby,

    /// <summary>The clock does not match what the VPN state requires.</summary>
    Mismatch,

    /// <summary>Enforcement paused by the user for a while.</summary>
    Paused,

    /// <summary>Every rule switched off in settings.</summary>
    Off,

    /// <summary>No key and the trial is over. Protection is not running.</summary>
    Locked
}

/// <summary>A single poll of the machine: what is true right now.</summary>
public sealed class GuardInput
{
    public bool VpnConnected { get; init; }
    public string VpnDetail { get; init; } = string.Empty;
    public int ClaudeProcessCount { get; init; }
    public string CurrentTimeZoneId { get; init; } = string.Empty;
    public DateTimeOffset Now { get; init; } = DateTimeOffset.Now;

    /// <summary>
    /// False once the trial is over with no active key. The guard then does
    /// nothing at all: no stopping, no clock changes, no firewall. Buying and
    /// ordering stay open, which is the whole point of letting people install
    /// first.
    /// </summary>
    public bool Licensed { get; init; } = true;
}

/// <summary>What the guard remembers between polls.</summary>
public sealed class GuardState
{
    /// <summary>User asked for home time back while Claude is closed.</summary>
    public bool HomeTimeHold { get; set; }

    /// <summary>Time-zone rules switched off until the app restarts.</summary>
    public bool TimeZoneSuspended { get; set; }

    /// <summary>All enforcement paused until this moment.</summary>
    public DateTimeOffset? PausedUntil { get; set; }

    /// <summary>Consecutive polls with no VPN.</summary>
    public int VpnMisses { get; set; }

    /// <summary>Time zone the guard already tried to switch to, to avoid asking twice.</summary>
    public string? TimeZoneAttempted { get; set; }

    /// <summary>Last switch attempt failed (declined UAC, or it did not stick).</summary>
    public bool TimeZoneChangeFailed { get; set; }

    /// <summary>While set, Claude is living on borrowed time: blocked, not yet closed.</summary>
    public DateTimeOffset? GraceUntil { get; set; }

    /// <summary>Which rule started the countdown.</summary>
    public string GraceReasonKey { get; set; } = string.Empty;

    public void Reset()
    {
        HomeTimeHold = false;
        TimeZoneSuspended = false;
        PausedUntil = null;
        VpnMisses = 0;
        TimeZoneAttempted = null;
        TimeZoneChangeFailed = false;
        GraceUntil = null;
        GraceReasonKey = string.Empty;
    }
}

/// <summary>What the app should do about this poll.</summary>
public sealed class GuardDecision
{
    public GuardPhase Phase { get; init; }
    public bool ShouldStopClaude { get; init; }
    public string StopReasonKey { get; init; } = string.Empty;
    public string StopReasonDetail { get; init; } = string.Empty;
    public string TargetTimeZoneId { get; init; } = string.Empty;
    public bool TimeZoneSynced { get; init; }
    public bool ShouldChangeTimeZone { get; init; }
    public bool NeedsTimeZoneConsent { get; init; }
    public bool SafeToOpenClaude { get; init; }
    public bool VpnConsideredDown { get; init; }
    public string HeadlineKey { get; init; } = string.Empty;

    /// <summary>A rule is broken and the countdown before closing Claude is running.</summary>
    public bool InGrace { get; init; }

    public DateTimeOffset? GraceEndsAt { get; init; }

    public string GraceReasonKey { get; init; } = string.Empty;
}

/// <summary>
/// The safety rules, as one pure function. No timers, no processes, no registry,
/// so the whole decision table can be tested directly.
/// </summary>
public static class GuardEngine
{
    public static string ResolveTargetTimeZone(GuardSettings settings, GuardState state, GuardInput input, bool vpnUpForTimeZone)
    {
        if (!vpnUpForTimeZone)
        {
            return settings.Active.HomeTimeZoneId;
        }

        if (state.HomeTimeHold && input.ClaudeProcessCount == 0)
        {
            return settings.Active.HomeTimeZoneId;
        }

        return settings.Active.RequiredTimeZoneId;
    }

    public static GuardDecision Evaluate(GuardInput input, GuardSettings settings, GuardState state)
    {
        // Opening Claude releases a home-time hold: the user clearly wants to work.
        if (input.ClaudeProcessCount > 0)
        {
            state.HomeTimeHold = false;
        }

        state.VpnMisses = input.VpnConnected ? 0 : state.VpnMisses + 1;

        var paused = state.PausedUntil is { } until && input.Now < until;
        var enforcing = settings.EnforceVpn || settings.Active.EnforceTimeZone;
        var timeZoneEnforced = settings.Active.EnforceTimeZone && !state.TimeZoneSuspended && !paused;
        var vpnEnforced = settings.EnforceVpn && !paused;

        // A blip must not flip the system clock, but it does block Claude at once.
        var vpnUpForTimeZone = input.VpnConnected || state.VpnMisses < Math.Max(1, settings.VpnMissTolerance);

        var target = ResolveTargetTimeZone(settings, state, input, vpnUpForTimeZone);
        var synced = string.Equals(input.CurrentTimeZoneId, target, StringComparison.OrdinalIgnoreCase);

        if (synced)
        {
            state.TimeZoneAttempted = null;
            state.TimeZoneChangeFailed = false;
        }

        var stop = false;
        var reasonKey = string.Empty;
        var reasonDetail = string.Empty;

        if (vpnEnforced && !input.VpnConnected && input.ClaudeProcessCount > 0)
        {
            stop = true;
            reasonKey = "Reason_VpnDown";
            reasonDetail = string.Empty;
        }
        else if (timeZoneEnforced
                 && input.VpnConnected
                 && input.ClaudeProcessCount > 0
                 && !string.Equals(input.CurrentTimeZoneId, settings.Active.RequiredTimeZoneId, StringComparison.OrdinalIgnoreCase))
        {
            stop = true;
            reasonKey = "Reason_TimeZone";
            reasonDetail = input.CurrentTimeZoneId;
        }

        if (settings.Action == EnforcementAction.WarnOnly)
        {
            stop = false;
        }

        // The traffic is already cut by the time this runs, so closing the
        // process can wait a few seconds for the tunnel to come back.
        var inGrace = false;
        DateTimeOffset? graceEndsAt = null;

        if (stop && settings.GraceSeconds > 0)
        {
            state.GraceUntil ??= input.Now.AddSeconds(settings.GraceSeconds);

            if (string.IsNullOrEmpty(state.GraceReasonKey))
            {
                state.GraceReasonKey = reasonKey;
            }

            if (input.Now < state.GraceUntil)
            {
                inGrace = true;
                graceEndsAt = state.GraceUntil;
                stop = false;
            }
            else
            {
                // Time is up. Let the stop through and clear the countdown.
                state.GraceUntil = null;
                state.GraceReasonKey = string.Empty;
            }
        }
        else
        {
            state.GraceUntil = null;
            state.GraceReasonKey = string.Empty;
        }

        var changeTimeZone = timeZoneEnforced
                             && !synced
                             && settings.TimeZoneMode != TimeZoneMode.ReportOnly
                             && !string.Equals(state.TimeZoneAttempted, target, StringComparison.OrdinalIgnoreCase);

        var safe = (!vpnEnforced || input.VpnConnected)
                   && (!timeZoneEnforced
                       || string.Equals(input.CurrentTimeZoneId, settings.Active.RequiredTimeZoneId, StringComparison.OrdinalIgnoreCase));

        GuardPhase phase;
        string headline;

        if (!input.Licensed)
        {
            // Nothing is enforced, and nothing is reported as protected either.
            // Saying "Ready" here would be a lie that costs someone their account.
            return new GuardDecision
            {
                Phase = GuardPhase.Locked,
                HeadlineKey = "Head_Locked",
                TargetTimeZoneId = input.CurrentTimeZoneId,
                TimeZoneSynced = true,
                SafeToOpenClaude = true,
                VpnConsideredDown = !input.VpnConnected
            };
        }

        if (!enforcing)
        {
            phase = GuardPhase.Off;
            headline = "Head_Off";
            safe = true;
        }
        else if (paused)
        {
            phase = GuardPhase.Paused;
            headline = "Head_Paused";
            safe = true;
        }
        else if (vpnEnforced && !input.VpnConnected)
        {
            phase = GuardPhase.Blocked;
            headline = "Head_Blocked";
        }
        else if (timeZoneEnforced && state.HomeTimeHold && input.ClaudeProcessCount == 0 && synced)
        {
            phase = GuardPhase.Standby;
            headline = "Head_Standby";
        }
        else if (timeZoneEnforced
                 && !string.Equals(input.CurrentTimeZoneId, settings.Active.RequiredTimeZoneId, StringComparison.OrdinalIgnoreCase))
        {
            phase = GuardPhase.Mismatch;
            headline = "Head_Mismatch";
        }
        else
        {
            phase = GuardPhase.Ready;
            headline = "Head_Ready";
        }

        return new GuardDecision
        {
            Phase = phase,
            ShouldStopClaude = stop,
            StopReasonKey = reasonKey,
            StopReasonDetail = reasonDetail,
            TargetTimeZoneId = target,
            TimeZoneSynced = synced,
            ShouldChangeTimeZone = changeTimeZone,
            NeedsTimeZoneConsent = changeTimeZone && settings.TimeZoneMode == TimeZoneMode.Ask,
            SafeToOpenClaude = safe,
            VpnConsideredDown = !input.VpnConnected,
            HeadlineKey = headline,
            InGrace = inGrace,
            GraceEndsAt = graceEndsAt,
            GraceReasonKey = inGrace ? state.GraceReasonKey : string.Empty
        };
    }
}
