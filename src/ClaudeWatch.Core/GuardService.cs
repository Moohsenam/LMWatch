namespace ClaudeWatch.Core;

/// <summary>Everything the UI needs to draw one moment in time.</summary>
public sealed class GuardSnapshot
{
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
    public GuardPhase Phase { get; init; }
    public string HeadlineKey { get; init; } = string.Empty;
    public bool VpnConnected { get; init; }
    public string VpnDetail { get; init; } = string.Empty;
    public IReadOnlyList<AdapterInfo> Adapters { get; init; } = Array.Empty<AdapterInfo>();
    public IReadOnlyList<ClaudeProcess> Processes { get; init; } = Array.Empty<ClaudeProcess>();
    public string CurrentTimeZoneId { get; init; } = string.Empty;
    public string TargetTimeZoneId { get; init; } = string.Empty;
    public bool TimeZoneSynced { get; init; }
    public bool SafeToOpenClaude { get; init; }
    public bool AwaitingTimeZoneConsent { get; init; }
    public bool TimeZoneSuspended { get; init; }
    public bool HomeTimeHold { get; init; }
    public DateTimeOffset? PausedUntil { get; init; }
    public bool LastChangeFailed { get; init; }
    public int StoppedThisTick { get; init; }

    /// <summary>The address the internet currently sees, refreshed on its own slower timer.</summary>
    public IpInfo? Ip { get; init; }

    /// <summary>The tunnel is up but the address is the one seen without it.</summary>
    public bool IpLeak { get; init; }

    /// <summary>Firewall rule state: true blocked, false open, null unknown.</summary>
    public bool? FirewallBlocking { get; init; }

    public bool FirewallReady { get; init; }

    /// <summary>Traffic is cut and the countdown before closing Claude is running.</summary>
    public bool InGrace { get; init; }

    public DateTimeOffset? GraceEndsAt { get; init; }

    /// <summary>Which rule started it: Reason_VpnDown or Reason_TimeZone.</summary>
    public string GraceReasonKey { get; init; } = string.Empty;
}

/// <summary>
/// The running guard: polls on a timer, applies the decision, writes the log and
/// hands the UI a snapshot. All events are raised on a background thread.
/// </summary>
public sealed class GuardService : IDisposable
{
    private readonly VpnDetector _vpn = new();
    private readonly ProcessWatcher _processes = new();
    private readonly TimeZoneController _timeZone = new();
    private readonly IpProbe _ipProbe = new();
    private readonly ActivityLog _log;
    private readonly object _gate = new();

    private System.Threading.Timer? _timer;
    private GuardSettings _settings;
    private GuardState _state = new();
    private bool _polling;
    private GuardPhase _lastPhase = GuardPhase.Off;
    private bool _lastVpn;
    private bool _firstPollDone;
    private IpInfo? _ip;
    private DateTimeOffset _lastIpCheck = DateTimeOffset.MinValue;
    private int _ipBusy;
    private bool? _firewallState;
    private bool _firewallReady;
    private DateTimeOffset _lastFirewallCheck = DateTimeOffset.MinValue;
    private System.Threading.Timer? _graceTimer;
    private bool _graceAnnounced;

    public GuardService(GuardSettings settings, ActivityLog log)
    {
        _settings = settings;
        _log = log;
    }

    public event EventHandler<GuardSnapshot>? Updated;
    public event EventHandler<ActivityEvent>? Notified;

    public GuardSnapshot? Latest { get; private set; }
    public GuardState State => _state;
    public TimeZoneController TimeZone => _timeZone;
    public bool IsRunning => _timer is not null;

    public GuardSettings Settings
    {
        get => _settings;
        set
        {
            _settings = value;
            Restart();
        }
    }

    public void Start()
    {
        Stop();
        var interval = _settings.Refresh;
        _timer = new System.Threading.Timer(_ => Poll(), null, TimeSpan.Zero, interval);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public void Restart()
    {
        if (IsRunning)
        {
            Start();
        }
    }

    public void PollNow() => Poll();

    /// <summary>Suspends every rule for a while. The countdown shows in the UI.</summary>
    public void Pause(TimeSpan duration)
    {
        _state.PausedUntil = DateTimeOffset.Now + duration;
        _log.Add(ActivityKind.Warning, "Protection paused", $"For {Describe(duration)}.");
        Poll();
    }

    public void Resume()
    {
        if (_state.PausedUntil is null)
        {
            return;
        }

        _state.PausedUntil = null;
        _log.Add(ActivityKind.Good, "Protection resumed");
        Poll();
    }

    /// <summary>Hold home time while Claude is closed (the original's [I] key).</summary>
    public void HoldHomeTime()
    {
        _state.HomeTimeHold = true;
        _state.TimeZoneSuspended = false;
        _state.TimeZoneAttempted = null;
        _log.Add(ActivityKind.Info, "Home time requested", "Held until Claude opens again.");
        Poll();
    }

    /// <summary>Get ready for a Claude session (the original's [T] key).</summary>
    public void PrepareWorkTime()
    {
        _state.HomeTimeHold = false;
        _state.TimeZoneSuspended = false;
        _state.TimeZoneAttempted = null;
        _state.TimeZoneChangeFailed = false;
        _log.Add(ActivityKind.Info, "Preparing work time zone");
        Poll();
    }

    /// <summary>Turn off time-zone rules until the app restarts.</summary>
    public void SuspendTimeZoneRules()
    {
        _state.TimeZoneSuspended = true;
        _state.TimeZoneAttempted = null;
        _log.Add(ActivityKind.Warning, "Time-zone protection skipped", "VPN protection is still on.");
        Poll();
    }

    public void ResumeTimeZoneRules()
    {
        _state.TimeZoneSuspended = false;
        _state.TimeZoneAttempted = null;
        _log.Add(ActivityKind.Good, "Time-zone protection restored");
        Poll();
    }

    public StopResult StopClaude(string reason)
    {
        var result = _processes.StopAll(_settings);

        if (result.Stopped > 0)
        {
            _log.Add(ActivityKind.Alert, $"Stopped {result.Stopped} Claude process(es)", reason);
        }
        else if (result.Requested > 0)
        {
            _log.Add(ActivityKind.Warning, "Could not stop Claude",
                result.Failures.Count > 0 ? result.Failures[0] : reason);
        }

        Poll();
        return result;
    }

    public TimeZoneChangeResult ApplyTimeZone(string timeZoneId)
    {
        _state.TimeZoneAttempted = timeZoneId;
        var result = _timeZone.Apply(timeZoneId, _settings);

        switch (result.Outcome)
        {
            case TimeZoneChangeOutcome.Changed:
                _state.TimeZoneChangeFailed = false;
                _log.Add(ActivityKind.Good, "Time zone changed", Friendly(timeZoneId));
                break;
            case TimeZoneChangeOutcome.AlreadyCorrect:
                _state.TimeZoneChangeFailed = false;
                break;
            case TimeZoneChangeOutcome.Declined:
                _state.TimeZoneChangeFailed = true;
                _log.Add(ActivityKind.Warning, "Time-zone change declined", "Claude stays blocked.");
                break;
            default:
                _state.TimeZoneChangeFailed = true;
                _log.Add(ActivityKind.Alert, "Time-zone change failed", result.Message);
                break;
        }

        Poll();
        return result;
    }

    public void ResetState()
    {
        _state = new GuardState();
        Poll();
    }

    private void Poll()
    {
        lock (_gate)
        {
            if (_polling)
            {
                return;
            }

            _polling = true;
        }

        try
        {
            var vpn = _vpn.Scan(_settings);
            var processes = _processes.Scan(_settings);
            var currentTz = _timeZone.CurrentId;

            var input = new GuardInput
            {
                VpnConnected = vpn.Connected,
                VpnDetail = vpn.Detail,
                ClaudeProcessCount = processes.Count,
                CurrentTimeZoneId = currentTz,
                Now = DateTimeOffset.Now
            };

            if (_state.PausedUntil is { } until && input.Now >= until)
            {
                _state.PausedUntil = null;
                _log.Add(ActivityKind.Good, "Protection resumed", "The pause ran out.");
            }

            var decision = GuardEngine.Evaluate(input, _settings, _state);
            var stoppedNow = 0;

            if (decision.ShouldStopClaude)
            {
                var reason = decision.StopReasonKey == "Reason_TimeZone"
                    ? $"The time zone is {Friendly(decision.StopReasonDetail)}, not {Friendly(_settings.RequiredTimeZoneId)}."
                    : "The VPN is not connected.";

                var result = _processes.StopAll(_settings);
                stoppedNow = result.Stopped;

                if (result.Stopped > 0)
                {
                    var entry = _log.Add(ActivityKind.Alert, $"Claude stopped automatically", reason);
                    Notified?.Invoke(this, entry);
                    processes = _processes.Scan(_settings);
                    input = new GuardInput
                    {
                        VpnConnected = vpn.Connected,
                        VpnDetail = vpn.Detail,
                        ClaudeProcessCount = processes.Count,
                        CurrentTimeZoneId = currentTz,
                        Now = input.Now
                    };
                    decision = GuardEngine.Evaluate(input, _settings, _state);
                }
            }
            else if (_settings.Action == EnforcementAction.WarnOnly
                     && processes.Count > 0
                     && !decision.SafeToOpenClaude
                     && _lastPhase != decision.Phase)
            {
                var entry = _log.Add(ActivityKind.Warning, "Claude is running while unprotected",
                    "Warn-only mode is on, so nothing was stopped.");
                Notified?.Invoke(this, entry);
            }

            if (decision.ShouldChangeTimeZone && _settings.TimeZoneMode == TimeZoneMode.Automatic)
            {
                var result = ApplyTimeZoneInternal(decision.TargetTimeZoneId);
                if (result.Success)
                {
                    currentTz = _timeZone.CurrentId;
                    input = new GuardInput
                    {
                        VpnConnected = vpn.Connected,
                        VpnDetail = vpn.Detail,
                        ClaudeProcessCount = processes.Count,
                        CurrentTimeZoneId = currentTz,
                        Now = input.Now
                    };
                    decision = GuardEngine.Evaluate(input, _settings, _state);
                }
            }

            ApplyKillSwitch(decision.SafeToOpenClaude);
            MaybeRefreshAddress(vpn.Connected);

            var leak = vpn.Connected
                       && _ip is { Ok: true }
                       && !string.IsNullOrWhiteSpace(_settings.KnownHomeIp)
                       && string.Equals(_ip.Ip, _settings.KnownHomeIp, StringComparison.OrdinalIgnoreCase);

            var snapshot = new GuardSnapshot
            {
                At = input.Now,
                Phase = decision.Phase,
                HeadlineKey = decision.HeadlineKey,
                VpnConnected = vpn.Connected,
                VpnDetail = vpn.Detail,
                Adapters = vpn.AllAdapters,
                Processes = processes,
                CurrentTimeZoneId = currentTz,
                TargetTimeZoneId = decision.TargetTimeZoneId,
                TimeZoneSynced = decision.TimeZoneSynced,
                SafeToOpenClaude = decision.SafeToOpenClaude,
                AwaitingTimeZoneConsent = decision.NeedsTimeZoneConsent,
                TimeZoneSuspended = _state.TimeZoneSuspended,
                HomeTimeHold = _state.HomeTimeHold,
                PausedUntil = _state.PausedUntil,
                LastChangeFailed = _state.TimeZoneChangeFailed,
                StoppedThisTick = stoppedNow,
                Ip = _ip,
                IpLeak = leak,
                FirewallBlocking = _firewallState,
                FirewallReady = _firewallReady,
                InGrace = decision.InGrace,
                GraceEndsAt = decision.GraceEndsAt,
                GraceReasonKey = decision.GraceReasonKey
            };

            TrackGrace(decision);

            TrackTransitions(snapshot);

            Latest = snapshot;
            Updated?.Invoke(this, snapshot);
        }
        catch
        {
            // A failed poll must never take the app down; the next tick retries.
        }
        finally
        {
            lock (_gate)
            {
                _polling = false;
            }
        }
    }

    private TimeZoneChangeResult ApplyTimeZoneInternal(string timeZoneId)
    {
        _state.TimeZoneAttempted = timeZoneId;
        var result = _timeZone.Apply(timeZoneId, _settings);

        if (result.Outcome == TimeZoneChangeOutcome.Changed)
        {
            _state.TimeZoneChangeFailed = false;
            var entry = _log.Add(ActivityKind.Good, "Time zone changed", Friendly(timeZoneId));
            Notified?.Invoke(this, entry);
        }
        else if (result.Outcome is TimeZoneChangeOutcome.Declined or TimeZoneChangeOutcome.Failed)
        {
            _state.TimeZoneChangeFailed = true;
            _log.Add(ActivityKind.Warning, "Time zone was not changed", result.Message);
        }

        return result;
    }


    /// <summary>
    /// Announces the countdown once, and makes sure a poll lands the moment it
    /// runs out so the process closes on the second the screen counted down to.
    /// </summary>
    private void TrackGrace(GuardDecision decision)
    {
        if (decision.InGrace)
        {
            if (!_graceAnnounced)
            {
                _graceAnnounced = true;

                var reason = decision.GraceReasonKey == "Reason_TimeZone"
                    ? "The clock is wrong."
                    : "The VPN dropped.";

                var entry = _log.Add(ActivityKind.Alert, "Traffic cut, Claude closing shortly",
                    $"{reason} Reconnect within {_settings.GraceSeconds}s to keep it open.");
                Notified?.Invoke(this, entry);
            }

            if (decision.GraceEndsAt is { } endsAt)
            {
                var wait = endsAt - DateTimeOffset.Now;
                if (wait < TimeSpan.Zero)
                {
                    wait = TimeSpan.Zero;
                }

                _graceTimer?.Dispose();
                _graceTimer = new System.Threading.Timer(
                    _ => Poll(), null, wait + TimeSpan.FromMilliseconds(120), Timeout.InfiniteTimeSpan);
            }

            return;
        }

        _graceTimer?.Dispose();
        _graceTimer = null;

        if (_graceAnnounced)
        {
            _graceAnnounced = false;

            if (decision.SafeToOpenClaude)
            {
                var entry = _log.Add(ActivityKind.Good, "Back in time", "Claude was left running.");
                Notified?.Invoke(this, entry);
            }
        }
    }

    /// <summary>
    /// Keeps the firewall rule in step with the guard. Only fires when the answer
    /// changes, so a normal tick costs nothing.
    /// </summary>
    private void ApplyKillSwitch(bool safe)
    {
        if (DateTimeOffset.Now - _lastFirewallCheck > TimeSpan.FromSeconds(20))
        {
            _lastFirewallCheck = DateTimeOffset.Now;
            _firewallReady = FirewallController.IsReady();
        }

        if (!_settings.EnableFirewallKillSwitch)
        {
            // Switched off while a block was in place: lift it once and forget it.
            if (_firewallState == true && FirewallController.Set(false, allowPrompt: false))
            {
                _firewallState = false;
                _log.Add(ActivityKind.Info, "Firewall block lifted", "The kill switch was switched off.");
            }

            return;
        }

        var shouldBlock = !safe;
        if (_firewallState == shouldBlock)
        {
            return;
        }

        if (!FirewallController.Set(shouldBlock, allowPrompt: false))
        {
            _log.Add(ActivityKind.Warning, "Firewall rule did not change",
                "Set the kill switch up again in Settings.");
            return;
        }

        _firewallState = shouldBlock;

        var entry = shouldBlock
            ? _log.Add(ActivityKind.Alert, "Claude's traffic is blocked", "The firewall rule is on.")
            : _log.Add(ActivityKind.Good, "Claude's traffic is allowed again", "The firewall rule is off.");

        Notified?.Invoke(this, entry);
    }

    /// <summary>
    /// Asks the internet for the current address on its own slow schedule, off the
    /// poll thread. The address seen with no tunnel becomes the leak reference.
    /// </summary>
    private void MaybeRefreshAddress(bool vpnConnected)
    {
        if (!_settings.ShowIpPanel)
        {
            return;
        }

        var due = DateTimeOffset.Now - _lastIpCheck >= TimeSpan.FromSeconds(Math.Clamp(_settings.IpRefreshSeconds, 15, 3600));
        if (!due || Interlocked.Exchange(ref _ipBusy, 1) == 1)
        {
            return;
        }

        _lastIpCheck = DateTimeOffset.Now;

        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                var info = await _ipProbe.LookupAsync().ConfigureAwait(false);
                var previous = _ip?.Ip;
                _ip = info;

                if (info.Ok && !vpnConnected && !string.IsNullOrWhiteSpace(info.Ip))
                {
                    _settings.KnownHomeIp = info.Ip;
                }

                if (info.Ok && previous is not null && previous != info.Ip)
                {
                    _log.Add(ActivityKind.Info, "Address changed", $"{previous} → {info.Ip}");
                }
            }
            catch
            {
                // The panel simply keeps the last answer.
            }
            finally
            {
                Interlocked.Exchange(ref _ipBusy, 0);
            }
        });
    }

    /// <summary>Forces an address lookup now, for the refresh button.</summary>
    public void RefreshAddressNow()
    {
        _lastIpCheck = DateTimeOffset.MinValue;
        Poll();
    }

    private void TrackTransitions(GuardSnapshot snapshot)
    {
        if (!_firstPollDone)
        {
            _firstPollDone = true;
            _lastPhase = snapshot.Phase;
            _lastVpn = snapshot.VpnConnected;
            _log.Add(ActivityKind.Info, "Monitoring started",
                snapshot.VpnConnected ? $"VPN: {snapshot.VpnDetail}" : "No VPN detected.");
            return;
        }

        if (snapshot.VpnConnected != _lastVpn)
        {
            var entry = snapshot.VpnConnected
                ? _log.Add(ActivityKind.Good, "VPN connected", snapshot.VpnDetail)
                : _log.Add(ActivityKind.Alert, "VPN disconnected", "Claude is blocked.");
            Notified?.Invoke(this, entry);
            _lastVpn = snapshot.VpnConnected;
        }

        if (snapshot.Phase != _lastPhase)
        {
            _lastPhase = snapshot.Phase;
        }
    }

    private static string Friendly(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return "unknown";
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId).DisplayName;
        }
        catch
        {
            return timeZoneId;
        }
    }

    private static string Describe(TimeSpan span)
        => span.TotalHours >= 1
            ? $"{span.TotalHours:0.#} hour(s)"
            : $"{span.TotalMinutes:0} minute(s)";

    public void Dispose()
    {
        _graceTimer?.Dispose();
        _graceTimer = null;
        Stop();
    }
}
