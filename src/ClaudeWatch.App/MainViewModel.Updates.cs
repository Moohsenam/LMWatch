using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ClaudeWatch.Core;

namespace ClaudeWatch.App;

/// <summary>
/// The update bar and what sits behind it. The app asks the server what the
/// current build is, and if there is a newer one it says so and waits. Nothing
/// is downloaded and nothing is installed until the customer asks for it.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly UpdateClient _updates = new();
    private System.Windows.Threading.DispatcherTimer? _updateTimer;
    private CancellationTokenSource? _downloadCancel;
    private bool _updateBusy;

    /// <summary>Hidden for this run after "not now", without forgetting the build.</summary>
    private bool _updateHidden;

    // ---------------------------------------------------------------- state

    public string AppVersion => UpdateClient.CurrentVersionText;

    public bool UpdateAvailable
        => !_updateHidden && _updates.Step is UpdateStep.Available or UpdateStep.Downloading
            or UpdateStep.Verifying or UpdateStep.Ready or UpdateStep.Installing;

    public bool UpdateWorking
        => _updates.Step is UpdateStep.Downloading or UpdateStep.Verifying or UpdateStep.Installing;

    public bool UpdateReady => _updates.Step == UpdateStep.Ready;

    public bool UpdateCanStart => _updates.Step == UpdateStep.Available && !_updateBusy;

    public int UpdatePercent => _updates.Percent;

    public bool UpdateHasError => _updates.Step == UpdateStep.Failed;

    /// <summary>The headline in the bar, which says what is happening now.</summary>
    public string UpdateTitle
    {
        get
        {
            var version = _updates.Available?.Version ?? string.Empty;

            return _updates.Step switch
            {
                UpdateStep.Downloading => L["Upd_Downloading"],
                UpdateStep.Verifying => L["Upd_Checking"],
                UpdateStep.Ready => L["Upd_Ready"],
                UpdateStep.Installing => L["Upd_Installing"],
                UpdateStep.Failed => L["Upd_Failed"],
                _ => string.Format(L["Upd_Available"], version)
            };
        }
    }

    /// <summary>The quieter second line: what changed, or how large it is.</summary>
    public string UpdateDetail
    {
        get
        {
            var release = _updates.Available;

            if (release is null)
            {
                return string.Empty;
            }

            if (_updates.Step == UpdateStep.Failed)
            {
                return _updates.Error == "checksum" ? L["Upd_Corrupt"] : L["Upd_NoReach"];
            }

            if (!string.IsNullOrWhiteSpace(release.Notes))
            {
                return release.Notes;
            }

            return release.Size > 0
                ? string.Format(L["Upd_Size"], release.Megabytes.ToString("0.#"))
                : string.Empty;
        }
    }

    /// <summary>What the Settings line says when there is nothing to install.</summary>
    public string UpdateStatusLine
    {
        get
        {
            if (_updates.Step == UpdateStep.Checking)
            {
                return L["Upd_Looking"];
            }

            if (UpdateAvailable)
            {
                return UpdateTitle;
            }

            return _updates.LastCheck == default
                ? string.Format(L["Upd_Current"], AppVersion)
                : string.Format(L["Upd_CurrentAt"], AppVersion, _updates.LastCheck.ToLocalTime().ToString("HH:mm"));
        }
    }

    // ------------------------------------------------------------- commands

    public RelayCommand CheckUpdateCommand { get; private set; } = null!;
    public RelayCommand StartUpdateCommand { get; private set; } = null!;
    public RelayCommand DismissUpdateCommand { get; private set; } = null!;

    private void BuildUpdateCommands()
    {
        CheckUpdateCommand = new RelayCommand(() => _ = CheckForUpdateAsync(quiet: false));

        StartUpdateCommand = new RelayCommand(() => _ = InstallUpdateAsync());

        DismissUpdateCommand = new RelayCommand(() =>
        {
            _downloadCancel?.Cancel();
            _updateHidden = true;
            RaiseUpdate();
        });
    }

    /// <summary>
    /// Starts the background checking. Once a few seconds after the window is
    /// up, so it never competes with startup, and every six hours after that.
    /// </summary>
    public void StartUpdateWatch()
    {
        UpdateClient.Sweep();

        if (!_settings.CheckForUpdates)
        {
            return;
        }

        _updateTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(20)
        };

        _updateTimer.Tick += (_, _) =>
        {
            // The first tick is the short one; every one after is the long wait.
            _updateTimer.Interval = TimeSpan.FromHours(6);
            _ = CheckForUpdateAsync(quiet: true);
        };

        _updateTimer.Start();
    }

    public void StopUpdateWatch()
    {
        _updateTimer?.Stop();
        _updateTimer = null;
        _downloadCancel?.Cancel();
    }

    // ------------------------------------------------------------ the steps

    public async Task CheckForUpdateAsync(bool quiet)
    {
        if (_updateBusy || UpdateWorking)
        {
            return;
        }

        if (quiet && !_settings.CheckForUpdates)
        {
            return;
        }

        _updateBusy = true;
        RaiseUpdate();

        try
        {
            var found = await _updates.CheckAsync(_settings.OrdersBaseUrl).ConfigureAwait(true);

            if (found is not null)
            {
                _updateHidden = false;
                _log.Add(ActivityKind.Info, string.Format(L["Upd_Logged"], found.Version));
            }
        }
        finally
        {
            _updateBusy = false;
            RaiseUpdate();
        }
    }

    private async Task InstallUpdateAsync()
    {
        if (_updates.Available is null || _updateBusy)
        {
            return;
        }

        _updateBusy = true;
        _downloadCancel = new CancellationTokenSource();

        try
        {
            var progress = new Progress<int>(_ => RaiseUpdate());

            RaiseUpdate();

            var got = await _updates
                .DownloadAsync(progress, _downloadCancel.Token)
                .ConfigureAwait(true);

            if (!got)
            {
                RaiseUpdate();
                return;
            }

            RaiseUpdate();

            // Windows asks for approval here. Declining leaves the downloaded
            // file where it is, so saying yes on the second try costs nothing.
            if (!_updates.Install())
            {
                RaiseUpdate();
                return;
            }

            _log.Add(ActivityKind.Info, string.Format(L["Upd_Started"], _updates.Available.Version));

            // The installer closes the app itself, but leaving first means it
            // never has to force anything, and the restart is clean.
            await Task.Delay(600).ConfigureAwait(true);
            Application.Current?.Shutdown();
        }
        finally
        {
            _updateBusy = false;
            _downloadCancel?.Dispose();
            _downloadCancel = null;
            RaiseUpdate();
        }
    }

    private void RaiseUpdate()
    {
        Raise(nameof(UpdateAvailable));
        Raise(nameof(UpdateWorking));
        Raise(nameof(UpdateReady));
        Raise(nameof(UpdateCanStart));
        Raise(nameof(UpdatePercent));
        Raise(nameof(UpdateHasError));
        Raise(nameof(UpdateTitle));
        Raise(nameof(UpdateDetail));
        Raise(nameof(UpdateStatusLine));
    }
}
