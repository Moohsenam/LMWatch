using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using ClaudeWatch.Core;

namespace ClaudeWatch.App;

public enum AppPage
{
    Dashboard,
    Buy,
    Orders,
    Licence,
    Usage,
    Processes,
    Network,
    Activity,
    Settings,
    About
}

public sealed class NotificationRequest
{
    public string Title { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public ActivityKind Kind { get; init; }
}

public sealed partial class MainViewModel : INotifyPropertyChanged
{
    public static readonly double[] IntervalChoices = { 0.5, 1, 2, 3, 5, 10 };
    public static readonly int[] ToleranceChoices = { 1, 2, 3, 5, 10 };
    public static readonly int[] RetentionChoices = { 7, 14, 30, 90, 365 };
    public static readonly string[] AccentChoices = { "#D97757", "#C9A227", "#5FA97C", "#7C9CC9", "#A98BC9", "#C05E5E" };

    private readonly SettingsStore _store = new();
    private readonly ActivityLog _log = new();
    private readonly System.Windows.Threading.DispatcherTimer _clock = new();

    private GuardSettings _settings;
    private GuardSnapshot? _snapshot;
    private AppPage _page = AppPage.Dashboard;
    private string _processSignature = string.Empty;
    private string _adapterSignature = string.Empty;
    private bool _savedFlash;

    public MainViewModel()
    {
        _settings = _store.Load();

        if (string.IsNullOrWhiteSpace(_settings.Active.ExecutablePath))
        {
            _settings.Active.ExecutablePath = ClaudeLauncher.Detect();
        }

        L.Language = _settings.Language;
        Edit = _settings.Clone();

        _log.LoadRecent();
        _log.Prune(_settings.LogRetentionDays);
        foreach (var item in _log.Items)
        {
            Events.Add(item);
        }

        Guard = new GuardService(_settings, _log);
        Guard.Updated += OnGuardUpdated;
        _log.Added += OnLogAdded;

        TimeZones = TimeZoneController.List();
        HelperInstalled = PrivilegedHelper.IsInstalled();

        _clock.Interval = TimeSpan.FromSeconds(1);
        _clock.Tick += (_, _) => RefreshClocks();
        _clock.Start();

        BuildCommands();
        BuildFeatureCommands();
        _ = LoadOrderServiceAsync();
    }

    // ------------------------------------------------------------- services

    public GuardService Guard { get; }

    public Strings L { get; } = new();

    public ActivityLog Log => _log;

    public GuardSettings Live => _settings;

    /// <summary>The working copy the Settings page edits until Save is pressed.</summary>
    public GuardSettings Edit { get; private set; }

    public IReadOnlyList<TimeZoneOption> TimeZones { get; }

    public string[] AccentOptions => AccentChoices;

    public ObservableCollection<ClaudeProcess> Processes { get; } = new();

    public ObservableCollection<AdapterInfo> Adapters { get; } = new();

    public ObservableCollection<ActivityEvent> Events { get; } = new();

    public event EventHandler<NotificationRequest>? NotificationRequested;

    public event EventHandler? ThemeChanged;

    public void Start()
    {
        Guard.Start();
        ApplyStartupRegistration();
    }

    public void Shutdown()
    {
        _clock.Stop();
        Guard.Dispose();
        _store.Save(_settings);
    }

    // ------------------------------------------------------------ navigation

    public AppPage Page
    {
        get => _page;
        set
        {
            if (_page == value)
            {
                return;
            }

            _page = value;
            Raise(nameof(Page));
            Raise(nameof(IsDashboard));
            Raise(nameof(IsBuy));
            Raise(nameof(IsOrders));
            Raise(nameof(IsLicence));
            Raise(nameof(IsUsage));
            Raise(nameof(IsProcesses));
            Raise(nameof(IsNetwork));
            Raise(nameof(IsActivity));
            Raise(nameof(IsSettings));
            Raise(nameof(IsAbout));

            // Prices are fetched the first time the page is opened, not at
            // startup: most sessions never go near it.
            if (value == AppPage.Buy && Prices.Count == 0 && !PricesLoading)
            {
                _ = LoadPricesAsync();
            }
        }
    }

    public bool IsDashboard => _page == AppPage.Dashboard;
    public bool IsBuy => _page == AppPage.Buy;
    public bool IsOrders => _page == AppPage.Orders;
    public bool IsLicence => _page == AppPage.Licence;
    public bool IsUsage => _page == AppPage.Usage;
    public bool IsProcesses => _page == AppPage.Processes;
    public bool IsNetwork => _page == AppPage.Network;
    public bool IsActivity => _page == AppPage.Activity;
    public bool IsSettings => _page == AppPage.Settings;
    public bool IsAbout => _page == AppPage.About;

    public FlowDirection Flow => L.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    // --------------------------------------------------------- status values

    public string StatusTitle => L["Head_" + Suffix()];
    public string StatusMessage => L["Msg_" + Suffix()];
    public Brush StatusBrush => Palette(PhaseBrushKey());
    public Brush StatusSoftBrush => Palette(PhaseBrushKey() + "Soft");

    public string VpnValue => _snapshot is null
        ? "…"
        : _snapshot.VpnConnected ? _snapshot.VpnDetail : L["Vpn_None"];

    public Brush VpnBrush => _snapshot?.VpnConnected == true ? Palette("Good") : Palette("Alert");

    public string ClockValue => Friendly(_snapshot?.CurrentTimeZoneId ?? string.Empty);

    public string ClockSub
    {
        get
        {
            if (_snapshot is null)
            {
                return string.Empty;
            }

            return _snapshot.TimeZoneSynced
                ? L["Clock_Matches"]
                : $"{L["Clock_Target"]}: {Friendly(_snapshot.TargetTimeZoneId)}";
        }
    }

    public Brush ClockBrush
    {
        get
        {
            if (_snapshot is null)
            {
                return Palette("TextDim");
            }

            if (!_settings.Active.EnforceTimeZone || _snapshot.TimeZoneSuspended)
            {
                return Palette("TextDim");
            }

            return _snapshot.TimeZoneSynced ? Palette("Good") : Palette("Warn");
        }
    }

    public string ClaudeValue
    {
        get
        {
            var count = _snapshot?.Processes.Count ?? 0;
            return count == 0 ? L["Claude_None"] : $"{count} {L["Claude_Running"]}";
        }
    }

    public Brush ClaudeBrush
    {
        get
        {
            var count = _snapshot?.Processes.Count ?? 0;
            if (count == 0)
            {
                return Palette("TextDim");
            }

            return _snapshot?.SafeToOpenClaude == true ? Palette("Good") : Palette("Alert");
        }
    }

    public string HomeClock { get; private set; } = string.Empty;

    public string WorkClock { get; private set; } = string.Empty;

    public bool CanOpenClaude => _snapshot?.SafeToOpenClaude == true;

    public bool CanStopClaude => (_snapshot?.Processes.Count ?? 0) > 0;

    public bool ShowClockBanner => _snapshot?.AwaitingTimeZoneConsent == true;

    public bool ShowFailedBanner => _snapshot?.LastChangeFailed == true && _snapshot?.AwaitingTimeZoneConsent != true;

    public bool ShowPauseBanner => _snapshot?.PausedUntil is not null;

    public bool ShowStandbyActions => _snapshot?.Processes.Count == 0;

    public bool ClockRulesSuspended => _snapshot?.TimeZoneSuspended == true;

    public string PauseUntilText => _snapshot?.PausedUntil is { } until
        ? $"{L["Banner_Paused"]} {until.ToString("HH:mm", CultureInfo.InvariantCulture)}"
        : string.Empty;

    public string ClockBannerText => _snapshot is null
        ? string.Empty
        : $"{Friendly(_snapshot.CurrentTimeZoneId)}  →  {Friendly(_snapshot.TargetTimeZoneId)}";

    public IEnumerable<ActivityEvent> RecentEvents => Events.Take(6);

    public string HeaderSummary
    {
        get
        {
            if (_snapshot is null)
            {
                return string.Empty;
            }

            var count = _snapshot.Processes.Count;
            var claude = count == 0 ? L["Claude_None"] : $"{count} × {L["Tile_Claude"]}";
            var vpn = _snapshot.VpnConnected ? "VPN ✓" : "VPN ✕";
            return $"{vpn}   ·   {claude}";
        }
    }

    // -------------------------------------------------------- settings shims

    public int ActionIndex
    {
        get => (int)Edit.Action;
        set { Edit.Action = (EnforcementAction)Math.Clamp(value, 0, 1); Raise(nameof(ActionIndex)); }
    }

    public int TimeZoneModeIndex
    {
        get => (int)Edit.TimeZoneMode;
        set { Edit.TimeZoneMode = (TimeZoneMode)Math.Clamp(value, 0, 2); Raise(nameof(TimeZoneModeIndex)); }
    }

    public int ThemeIndex
    {
        get => (int)Edit.Theme;
        set
        {
            Edit.Theme = (AppTheme)Math.Clamp(value, 0, 2);
            Raise(nameof(ThemeIndex));
            _settings.Theme = Edit.Theme;
            ThemeChanged?.Invoke(this, EventArgs.Empty);
            RefreshBrushes();
        }
    }

    public int LanguageIndex
    {
        get => Edit.Language == "fa" ? 1 : 0;
        set
        {
            Edit.Language = value == 1 ? "fa" : "en";
            _settings.Language = Edit.Language;
            L.Language = Edit.Language;
            Raise(nameof(LanguageIndex));
            Raise(nameof(L));
            Raise(nameof(Flow));
            RefreshStatusText();
        }
    }

    public int IntervalIndex
    {
        get => Math.Max(0, Array.IndexOf(IntervalChoices, Edit.RefreshSeconds));
        set
        {
            var index = Math.Clamp(value, 0, IntervalChoices.Length - 1);
            Edit.RefreshSeconds = IntervalChoices[index];
            Raise(nameof(IntervalIndex));
        }
    }

    public int ToleranceIndex
    {
        get => Math.Max(0, Array.IndexOf(ToleranceChoices, Edit.VpnMissTolerance));
        set
        {
            var index = Math.Clamp(value, 0, ToleranceChoices.Length - 1);
            Edit.VpnMissTolerance = ToleranceChoices[index];
            Raise(nameof(ToleranceIndex));
        }
    }

    public int RetentionIndex
    {
        get => Math.Max(0, Array.IndexOf(RetentionChoices, Edit.LogRetentionDays));
        set
        {
            var index = Math.Clamp(value, 0, RetentionChoices.Length - 1);
            Edit.LogRetentionDays = RetentionChoices[index];
            Raise(nameof(RetentionIndex));
        }
    }

    public string ExtraNames
    {
        get => string.Join(", ", Edit.Active.ExtraProcessNames);
        set { Edit.Active.ExtraProcessNames = Split(value); Raise(nameof(ExtraNames)); }
    }

    public string ExcludedNames
    {
        get => string.Join(", ", Edit.Active.ExcludedProcessNames);
        set { Edit.Active.ExcludedProcessNames = Split(value); Raise(nameof(ExcludedNames)); }
    }

    public bool HelperInstalled { get; private set; }

    public string HelperStatus => HelperInstalled ? L["Set_Helper_On"] : L["Set_Helper_Off"];

    public bool SavedFlash
    {
        get => _savedFlash;
        private set { _savedFlash = value; Raise(nameof(SavedFlash)); }
    }

    public string ClaudePathDisplay => string.IsNullOrWhiteSpace(Edit.Active.ExecutablePath)
        ? L["Set_ClaudePath_Missing"]
        : Edit.Active.ExecutablePath;

    public string VersionText
    {
        get
        {
            var version = typeof(MainViewModel).Assembly.GetName().Version;
            return version is null ? "1.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public string ElevationText => TimeZoneController.IsElevated ? L["About_Admin"] : L["About_NotAdmin"];

    // ------------------------------------------------------------- commands

    public RelayCommand NavigateCommand { get; private set; } = null!;
    public RelayCommand OpenClaudeCommand { get; private set; } = null!;
    public RelayCommand StopClaudeCommand { get; private set; } = null!;
    public RelayCommand StopOneCommand { get; private set; } = null!;
    public RelayCommand PrepareCommand { get; private set; } = null!;
    public RelayCommand HoldHomeCommand { get; private set; } = null!;
    public RelayCommand PauseCommand { get; private set; } = null!;
    public RelayCommand ResumeCommand { get; private set; } = null!;
    public RelayCommand ChangeClockCommand { get; private set; } = null!;
    public RelayCommand SkipClockCommand { get; private set; } = null!;
    public RelayCommand RestoreClockCommand { get; private set; } = null!;
    public RelayCommand RefreshCommand { get; private set; } = null!;
    public RelayCommand TrustAdapterCommand { get; private set; } = null!;
    public RelayCommand IgnoreAdapterCommand { get; private set; } = null!;
    public RelayCommand SaveCommand { get; private set; } = null!;
    public RelayCommand ResetCommand { get; private set; } = null!;
    public RelayCommand ExportLogCommand { get; private set; } = null!;
    public RelayCommand ClearLogCommand { get; private set; } = null!;
    public RelayCommand InstallHelperCommand { get; private set; } = null!;
    public RelayCommand RemoveHelperCommand { get; private set; } = null!;
    public RelayCommand OpenDataFolderCommand { get; private set; } = null!;
    public RelayCommand BrowseClaudeCommand { get; private set; } = null!;
    public RelayCommand OpenSourceCommand { get; private set; } = null!;
    public RelayCommand AccentCommand { get; private set; } = null!;

    private void BuildCommands()
    {
        NavigateCommand = new RelayCommand(p =>
        {
            if (p is string name && Enum.TryParse<AppPage>(name, out var page))
            {
                Page = page;
            }
        });

        OpenClaudeCommand = new RelayCommand(() =>
        {
            var (ok, message) = ClaudeLauncher.Launch(_settings);
            if (!ok)
            {
                MessageBox.Show(message, L["Confirm_Title"], MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                _log.Add(ActivityKind.Info, "Claude opened by hand");
            }
        }, () => CanOpenClaude);

        StopClaudeCommand = new RelayCommand(() =>
        {
            if (_settings.ConfirmBeforeStopping)
            {
                var answer = MessageBox.Show(L["Confirm_Stop"], L["Confirm_Title"],
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            Guard.StopClaude("Stopped by hand.");
        });

        StopOneCommand = new RelayCommand(p =>
        {
            if (p is ClaudeProcess process)
            {
                new ProcessWatcher().Stop(process.Pid, out _);
                _log.Add(ActivityKind.Warning, $"Stopped {process.Name}", $"PID {process.Pid}");
                Guard.PollNow();
            }
        });

        PrepareCommand = new RelayCommand(() => Guard.PrepareWorkTime());
        HoldHomeCommand = new RelayCommand(() => Guard.HoldHomeTime());

        PauseCommand = new RelayCommand(p =>
        {
            var minutes = 30d;
            if (p is string text && double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            {
                minutes = parsed;
            }

            Guard.Pause(TimeSpan.FromMinutes(minutes));
        });

        ResumeCommand = new RelayCommand(() => Guard.Resume());

        ChangeClockCommand = new RelayCommand(() =>
        {
            var target = _snapshot?.TargetTimeZoneId;
            if (!string.IsNullOrWhiteSpace(target))
            {
                Guard.ApplyTimeZone(target);
            }
        });

        SkipClockCommand = new RelayCommand(() => Guard.SuspendTimeZoneRules());
        RestoreClockCommand = new RelayCommand(() => Guard.ResumeTimeZoneRules());
        RefreshCommand = new RelayCommand(() => Guard.PollNow());

        TrustAdapterCommand = new RelayCommand(p =>
        {
            if (p is not AdapterInfo adapter)
            {
                return;
            }

            Toggle(_settings.TrustedAdapters, adapter.Name);
            Toggle(Edit.TrustedAdapters, adapter.Name, _settings.TrustedAdapters.Contains(adapter.Name));
            _settings.IgnoredAdapters.Remove(adapter.Name);
            Edit.IgnoredAdapters.Remove(adapter.Name);
            Persist();
        });

        IgnoreAdapterCommand = new RelayCommand(p =>
        {
            if (p is not AdapterInfo adapter)
            {
                return;
            }

            Toggle(_settings.IgnoredAdapters, adapter.Name);
            Toggle(Edit.IgnoredAdapters, adapter.Name, _settings.IgnoredAdapters.Contains(adapter.Name));
            _settings.TrustedAdapters.Remove(adapter.Name);
            Edit.TrustedAdapters.Remove(adapter.Name);
            Persist();
        });

        SaveCommand = new RelayCommand(SaveSettings);

        ResetCommand = new RelayCommand(() =>
        {
            var answer = MessageBox.Show(L["Confirm_Reset"], L["Confirm_Title"],
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }

            Edit = new GuardSettings();
            Edit.EnsureServices();
            Edit.Active.ExecutablePath = ClaudeLauncher.Detect();
            SaveSettings();
            RaiseAllSettings();
        });

        ExportLogCommand = new RelayCommand(() =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = $"claude-watch-activity-{DateTime.Now:yyyy-MM-dd}.txt",
                Filter = "Text file (*.txt)|*.txt|All files (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    File.WriteAllText(dialog.FileName, _log.ExportText());
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, L["Confirm_Title"], MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        });

        ClearLogCommand = new RelayCommand(() =>
        {
            _log.Clear();
            Events.Clear();
            Raise(nameof(RecentEvents));
        });

        InstallHelperCommand = new RelayCommand(() =>
        {
            var (ok, message) = PrivilegedHelper.Install(Edit);
            HelperInstalled = ok && PrivilegedHelper.IsInstalled();
            _settings.UsePrivilegedHelper = HelperInstalled;
            Edit.UsePrivilegedHelper = HelperInstalled;
            Persist();
            Raise(nameof(HelperInstalled));
            Raise(nameof(HelperStatus));

            if (!ok && !string.IsNullOrWhiteSpace(message))
            {
                MessageBox.Show(message, L["Confirm_Title"], MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else if (ok)
            {
                _log.Add(ActivityKind.Good, "Clock shortcut set up", "No more approval prompts.");
            }
        });

        RemoveHelperCommand = new RelayCommand(() =>
        {
            PrivilegedHelper.Uninstall();
            HelperInstalled = PrivilegedHelper.IsInstalled();
            _settings.UsePrivilegedHelper = HelperInstalled;
            Edit.UsePrivilegedHelper = HelperInstalled;
            Persist();
            Raise(nameof(HelperInstalled));
            Raise(nameof(HelperStatus));
        });

        OpenDataFolderCommand = new RelayCommand(() => Shell.OpenFolder(AppPaths.Root));

        BrowseClaudeCommand = new RelayCommand(() =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
                Title = L["Set_ClaudePath"]
            };

            if (dialog.ShowDialog() == true)
            {
                Edit.Active.ExecutablePath = dialog.FileName;
                _settings.Active.ExecutablePath = dialog.FileName;
                Persist();
                Raise(nameof(ClaudePathDisplay));
            }
        });

        OpenSourceCommand = new RelayCommand(() => Shell.OpenUrl("https://github.com/omidkorat/claude-watch"));

        AccentCommand = new RelayCommand(p =>
        {
            if (p is not string hex)
            {
                return;
            }

            Edit.AccentColor = hex;
            _settings.AccentColor = hex;
            Persist();
            ThemeChanged?.Invoke(this, EventArgs.Empty);
            RefreshBrushes();
        });
    }

    // -------------------------------------------------------------- actions

    private void SaveSettings()
    {
        var oldLanguage = _settings.Language;
        var zonesChanged = _settings.Active.RequiredTimeZoneId != Edit.Active.RequiredTimeZoneId
                           || _settings.Active.HomeTimeZoneId != Edit.Active.HomeTimeZoneId;

        _settings = SettingsStore.Sanitize(Edit.Clone());
        _store.Save(_settings);

        // The scheduled tasks carry the zone names, so they have to be rewritten
        // when those change — otherwise they would keep setting the old ones.
        if (zonesChanged && HelperInstalled)
        {
            var (ok, message) = PrivilegedHelper.Install(_settings);
            HelperInstalled = PrivilegedHelper.IsInstalled();
            Raise(nameof(HelperInstalled));
            Raise(nameof(HelperStatus));

            if (!ok && !string.IsNullOrWhiteSpace(message))
            {
                _log.Add(ActivityKind.Warning, "Clock shortcut needs setting up again", message);
            }
        }

        Guard.Settings = _settings;
        Guard.Start();

        ApplyStartupRegistration();
        _log.Prune(_settings.LogRetentionDays);

        L.Language = _settings.Language;
        if (oldLanguage != _settings.Language)
        {
            Raise(nameof(L));
            Raise(nameof(Flow));
        }

        ThemeChanged?.Invoke(this, EventArgs.Empty);
        RefreshBrushes();
        RefreshStatusText();
        RaiseAllSettings();

        SavedFlash = true;
        var reset = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        reset.Tick += (s, _) =>
        {
            SavedFlash = false;
            ((System.Windows.Threading.DispatcherTimer)s!).Stop();
        };
        reset.Start();
    }

    private void Persist()
    {
        _store.Save(_settings);
        Guard.Settings = _settings;
        Guard.Start();
        Guard.PollNow();
    }

    private void ApplyStartupRegistration()
    {
        StartupRegistration.Set(_settings.StartWithWindows, _settings.StartMinimized);
    }

    public void RequestStopFromTray() => Guard.StopClaude("Stopped from the tray.");

    /// <summary>Lets the window raise a tray balloon without knowing about the tray.</summary>
    public void NotifyFromWindow(string title, string message)
        => NotificationRequested?.Invoke(this, new NotificationRequest
        {
            Title = title,
            Message = message,
            Kind = ActivityKind.Info
        });

    // --------------------------------------------------------------- events

    private void OnGuardUpdated(object? sender, GuardSnapshot snapshot)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.Invoke(() =>
        {
            _snapshot = snapshot;

            var processSignature = string.Join("|", snapshot.Processes.Select(p => $"{p.Pid}:{p.MemoryBytes / 1048576}"));
            if (processSignature != _processSignature)
            {
                _processSignature = processSignature;
                Processes.Clear();
                foreach (var process in snapshot.Processes)
                {
                    Processes.Add(process);
                }
            }

            var adapterSignature = string.Join("|", snapshot.Adapters.Select(a => $"{a.Name}:{a.IsUp}:{a.CountsAsVpn}:{a.Trusted}:{a.Ignored}:{a.CarriesTraffic}"));
            if (adapterSignature != _adapterSignature)
            {
                _adapterSignature = adapterSignature;
                Adapters.Clear();
                foreach (var adapter in snapshot.Adapters)
                {
                    Adapters.Add(adapter);
                }
            }

            LearnClaudePathFromProcesses();
            RefreshStatusText();
            RefreshBrushes();
            OpenClaudeCommand.RaiseCanExecuteChanged();
        });
    }

    private void OnLogAdded(object? sender, ActivityEvent entry)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.Invoke(() =>
        {
            Events.Insert(0, entry);
            while (Events.Count > 500)
            {
                Events.RemoveAt(Events.Count - 1);
            }

            Raise(nameof(RecentEvents));

            if (!_settings.ShowNotifications)
            {
                return;
            }

            if (_settings.NotifyOnlyOnProblems && entry.Kind is ActivityKind.Info or ActivityKind.Good)
            {
                return;
            }

            if (entry.Kind == ActivityKind.Info)
            {
                return;
            }

            NotificationRequested?.Invoke(this, new NotificationRequest
            {
                Title = entry.Title,
                Message = entry.Detail,
                Kind = entry.Kind
            });
        });
    }

    private void RefreshClocks()
    {
        HomeClock = ClockIn(_settings.Active.HomeTimeZoneId);
        WorkClock = ClockIn(_settings.Active.RequiredTimeZoneId);
        Raise(nameof(HomeClock));
        Raise(nameof(WorkClock));

        if (_snapshot?.PausedUntil is not null)
        {
            Raise(nameof(PauseUntilText));
        }
    }

    private static string ClockIn(string timeZoneId)
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).ToString("HH:mm", CultureInfo.InvariantCulture);
        }
        catch
        {
            return "--:--";
        }
    }

    private void RefreshStatusText()
    {
        Raise(nameof(StatusTitle));
        Raise(nameof(StatusMessage));
        Raise(nameof(VpnValue));
        Raise(nameof(ClockValue));
        Raise(nameof(ClockSub));
        Raise(nameof(ClaudeValue));
        Raise(nameof(CanOpenClaude));
        Raise(nameof(CanStopClaude));
        Raise(nameof(ShowClockBanner));
        Raise(nameof(ShowFailedBanner));
        Raise(nameof(ShowPauseBanner));
        Raise(nameof(ShowStandbyActions));
        Raise(nameof(ClockRulesSuspended));
        Raise(nameof(PauseUntilText));
        Raise(nameof(ClockBannerText));
        Raise(nameof(HeaderSummary));
        Raise(nameof(HelperStatus));
        Raise(nameof(ElevationText));
        Raise(nameof(ClaudePathDisplay));
        RefreshFeatureText();
    }

    private void RefreshBrushes()
    {
        Raise(nameof(StatusBrush));
        Raise(nameof(StatusSoftBrush));
        Raise(nameof(VpnBrush));
        Raise(nameof(ClockBrush));
        Raise(nameof(ClaudeBrush));
    }

    private void RaiseAllSettings()
    {
        Raise(nameof(Edit));
        Raise(nameof(ActionIndex));
        Raise(nameof(TimeZoneModeIndex));
        Raise(nameof(ThemeIndex));
        Raise(nameof(LanguageIndex));
        Raise(nameof(IntervalIndex));
        Raise(nameof(ToleranceIndex));
        Raise(nameof(RetentionIndex));
        Raise(nameof(ExtraNames));
        Raise(nameof(ExcludedNames));
        Raise(nameof(ClaudePathDisplay));
    }

    // -------------------------------------------------------------- helpers

    public GuardPhase CurrentPhase => _snapshot?.Phase ?? GuardPhase.Off;

    private string Suffix() => _snapshot?.Phase switch
    {
        GuardPhase.Ready => "Ready",
        GuardPhase.Blocked => "Blocked",
        GuardPhase.Standby => "Standby",
        GuardPhase.Mismatch => "Mismatch",
        GuardPhase.Paused => "Paused",
        GuardPhase.Off => "Off",
        _ => "Off"
    };

    private string PhaseBrushKey() => _snapshot?.Phase switch
    {
        GuardPhase.Ready => "Good",
        GuardPhase.Blocked => "Alert",
        GuardPhase.Standby => "Info",
        GuardPhase.Mismatch => "Warn",
        GuardPhase.Paused => "Warn",
        _ => "TextDim"
    };

    public static Brush Palette(string key)
    {
        // TryFindResource walks merged dictionaries; ResourceDictionary.Contains does not.
        var found = Application.Current?.TryFindResource(key);
        return found as Brush ?? Brushes.Gray;
    }

    private static string Friendly(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return "—";
        }

        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            var display = zone.DisplayName;
            var close = display.IndexOf(')');
            return close > 0 && close + 2 < display.Length ? display[(close + 2)..] : display;
        }
        catch
        {
            return timeZoneId;
        }
    }

    private static List<string> Split(string value)
        => (value ?? string.Empty)
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(v => v.Trim())
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static void Toggle(List<string> list, string value, bool? force = null)
    {
        var present = list.Contains(value, StringComparer.OrdinalIgnoreCase);
        var shouldContain = force ?? !present;

        if (shouldContain && !present)
        {
            list.Add(value);
        }
        else if (!shouldContain && present)
        {
            list.RemoveAll(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
