using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using ClaudeWatch.Core;

namespace ClaudeWatch.App;

/// <summary>A plan as the order page shows it, with the label in the current language.</summary>
public sealed class OrderPlanChoice
{
    public string Key { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public override string ToString() => Text;
}

public sealed partial class MainViewModel
{
    public static readonly int[] GraceChoices = { 0, 10, 20, 30, 60 };
    public static readonly int[] IpRefreshChoices = { 30, 60, 300, 900 };
    private static readonly int[] MonthChoices = { 1, 3, 6, 12 };
    private static readonly string[] ContactKinds = { "telegram", "whatsapp", "email", "phone" };

    private readonly OrdersClient _orders = new();
    private readonly PricingClient _pricing = new();

    private bool _ipCopied;
    private bool _orderBusy;

    // ==================================================================== IP

    public bool ShowIpCard => _settings.ShowIpPanel;

    public string IpValue
    {
        get
        {
            var info = _snapshot?.Ip;
            if (info is null)
            {
                return L["Ip_Checking"];
            }

            return info.Ok && !string.IsNullOrWhiteSpace(info.Ip) ? info.Ip : L["Ip_Failed"];
        }
    }

    public string IpWhere => _snapshot?.Ip?.Where ?? string.Empty;

    public string IpNetwork => _snapshot?.Ip?.Network ?? string.Empty;

    public string IpSource
    {
        get
        {
            var info = _snapshot?.Ip;
            return info is { Ok: true } && !string.IsNullOrWhiteSpace(info.Source)
                ? $"{L["Ip_Source"]} {info.Source}"
                : string.Empty;
        }
    }

    public bool ShowIpLeak => _snapshot?.IpLeak == true;

    public Brush IpBrush
    {
        get
        {
            if (_snapshot?.IpLeak == true)
            {
                return Palette("Alert");
            }

            var info = _snapshot?.Ip;
            if (info is null || !info.Ok)
            {
                return Palette("TextDim");
            }

            return _snapshot?.VpnConnected == true ? Palette("Good") : Palette("Text");
        }
    }

    public string IpCopyLabel => _ipCopied ? L["Ip_Copied"] : L["Ip_Copy"];

    public string KnownHomeIp => string.IsNullOrWhiteSpace(_settings.KnownHomeIp)
        ? "—"
        : _settings.KnownHomeIp;

    public int IpRefreshIndex
    {
        get => Math.Max(0, Array.IndexOf(IpRefreshChoices, Edit.IpRefreshSeconds));
        set
        {
            var index = Math.Clamp(value, 0, IpRefreshChoices.Length - 1);
            Edit.IpRefreshSeconds = IpRefreshChoices[index];
            Raise(nameof(IpRefreshIndex));
        }
    }

    // ================================================= the countdown warning

    public bool GraceActive => _snapshot?.InGrace == true;

    public DateTimeOffset? GraceEndsAt => _snapshot?.GraceEndsAt;

    public int GraceTotalSeconds => Math.Max(1, _settings.GraceSeconds);

    public string GraceTitle => _snapshot?.GraceReasonKey == "Reason_TimeZone"
        ? L["Grace_Clock"]
        : L["Grace_Vpn"];

    public string GraceBody => _snapshot?.GraceReasonKey == "Reason_TimeZone"
        ? L["Grace_BodyClock"]
        : L["Grace_Body"];

    /// <summary>The reassuring line once the tunnel is back before the timer ran out.</summary>
    public string GraceResolvedText => _snapshot?.VpnConnected == true
        ? L["Grace_Back"]
        : L["Grace_Fixed"];

    public int GraceIndex
    {
        get
        {
            var index = Array.IndexOf(GraceChoices, Edit.GraceSeconds);
            return index < 0 ? 1 : index;
        }
        set
        {
            var index = Math.Clamp(value, 0, GraceChoices.Length - 1);
            Edit.GraceSeconds = GraceChoices[index];
            Raise(nameof(GraceIndex));
        }
    }

    // ============================================================ auto setup

    public bool SetupComplete { get; private set; }

    public string SetupStatus
    {
        get
        {
            if (SetupComplete)
            {
                return L["Setup_Ready"];
            }

            return PrivilegedHelper.IsInstalled() || FirewallController.IsReady(_settings.Active)
                ? L["Setup_Partial"]
                : L["Setup_Missing"];
        }
    }

    public Brush SetupBrush => SetupComplete ? Palette("Good") : Palette("Warn");

    public string ClaudeFoundVia { get; private set; } = string.Empty;

    /// <summary>
    /// Everything the app can arrange for itself: find Claude, pick a language,
    /// and register the privileged pieces in one approval. Runs on a background
    /// thread at startup so the window is up first.
    /// </summary>
    public async Task RunAutoSetupAsync(bool force = false)
    {
        // ---- 1. where each watched app lives
        foreach (var profile in _settings.Watched.ToList())
        {
            var current = profile.ExecutablePath;

            if (!force && !string.IsNullOrWhiteSpace(current) && File.Exists(current))
            {
                continue;
            }

            var key = profile.Key;
            var found = await Task.Run(() => ClaudeFinder.Find(key)).ConfigureAwait(true);

            if (found.Found)
            {
                profile.ExecutablePath = found.Path;

                if (Edit.ByKey(key) is { } mirror)
                {
                    mirror.ExecutablePath = found.Path;
                }

                if (string.Equals(key, _settings.ActiveServiceKey, StringComparison.OrdinalIgnoreCase))
                {
                    ClaudeFoundVia = found.Source;
                }

                _log.Add(ActivityKind.Good, $"{profile.Name} found", $"{found.Path} ({found.Source})");
            }
            else if (!_settings.FirstRunCompleted)
            {
                _log.Add(ActivityKind.Warning, $"{profile.Name} was not found");
            }
        }

        Raise(nameof(ClaudePathDisplay));
        Raise(nameof(ClaudeFoundVia));

        // ---- 2. language, on the very first run only
        if (!_settings.FirstRunCompleted &&
            CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("fa", StringComparison.OrdinalIgnoreCase))
        {
            _settings.Language = "fa";
            Edit.Language = "fa";
            L.Language = "fa";
            Raise(nameof(L));
            Raise(nameof(Flow));
            Raise(nameof(LanguageIndex));
        }

        // ---- 3. the parts that need administrator rights
        var tasksReady = PrivilegedHelper.IsInstalled();

        var located = _settings.Watched
            .Where(p => !string.IsNullOrWhiteSpace(p.ExecutablePath) && File.Exists(p.ExecutablePath))
            .ToList();

        var hasClaude = located.Count > 0;

        // Every located app needs its own rule, pointing at its own executable.
        var rulesReady = located.All(p =>
            FirewallController.IsReady(p) &&
            string.Equals(p.FirewallRulePath, p.ExecutablePath, StringComparison.OrdinalIgnoreCase));

        var complete = tasksReady && (!hasClaude || rulesReady);

        // Ask once. After that only a moved Claude, or the button in Settings,
        // brings the prompt back.
        var shouldRun = force
                        || (!complete && !_settings.FirstRunCompleted)
                        || (!complete && _settings.UsePrivilegedHelper && hasClaude && !rulesReady);

        if (!shouldRun)
        {
            SetupComplete = complete;
            FinishSetupState();
            return;
        }

        var (ok, message) = await Task
            .Run(() => PrivilegedHelper.InstallEverything(_settings))
            .ConfigureAwait(true);

        if (ok)
        {
            _settings.UsePrivilegedHelper = true;
            Edit.UsePrivilegedHelper = true;

            foreach (var profile in located)
            {
                profile.EnableFirewallKillSwitch = true;
                profile.FirewallRulePath = profile.ExecutablePath;

                if (Edit.ByKey(profile.Key) is { } mirror)
                {
                    mirror.EnableFirewallKillSwitch = true;
                    mirror.FirewallRulePath = profile.ExecutablePath;
                }
            }

            SetupComplete = PrivilegedHelper.IsInstalled()
                            && (!hasClaude || located.All(FirewallController.IsReady));
            _log.Add(ActivityKind.Good, "Setup finished",
                string.IsNullOrWhiteSpace(message) ? "No more approval prompts." : message);
        }
        else
        {
            SetupComplete = false;
            _log.Add(ActivityKind.Warning, "Setup was not completed",
                string.IsNullOrWhiteSpace(message) ? L["Setup_Declined"] : message);
        }

        FinishSetupState();
    }

    private void FinishSetupState()
    {
        _settings.FirstRunCompleted = true;
        Edit.FirstRunCompleted = true;

        HelperInstalled = PrivilegedHelper.IsInstalled();
        Persist();

        Raise(nameof(SetupComplete));
        Raise(nameof(SetupStatus));
        Raise(nameof(SetupBrush));
        Raise(nameof(HelperInstalled));
        Raise(nameof(HelperStatus));
        RaiseAllSettings();
        RefreshFeatureText();
    }

    /// <summary>Picks up Claude's path from a process that is running right now.</summary>
    private void LearnClaudePathFromProcesses()
    {
        if (!string.IsNullOrWhiteSpace(_settings.Active.ExecutablePath) &&
            File.Exists(_settings.Active.ExecutablePath))
        {
            return;
        }

        var running = _snapshot?.Processes.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Path));
        if (running is null)
        {
            return;
        }

        _settings.Active.ExecutablePath = running.Path;
        Edit.Active.ExecutablePath = running.Path;
        ClaudeFoundVia = "running process";
        _log.Add(ActivityKind.Good, L["Claude_Found"], running.Path);
        Raise(nameof(ClaudePathDisplay));
        Raise(nameof(ClaudeFoundVia));
    }

    // ============================================================== firewall

    public bool FirewallReady => _snapshot?.FirewallReady ?? false;

    public string FirewallStatus
    {
        get
        {
            if (!_settings.Active.EnableFirewallKillSwitch)
            {
                return L["Set_Firewall_Off"];
            }

            return _snapshot?.FirewallBlocking switch
            {
                true => L["Fw_Blocked"],
                false => L["Fw_Open"],
                _ => L["Fw_Unknown"]
            };
        }
    }

    public Brush FirewallBrush => _snapshot?.FirewallBlocking switch
    {
        true => Palette("Alert"),
        false => Palette("Good"),
        _ => Palette("TextDim")
    };

    public bool ShowFirewallChip => _settings.Active.EnableFirewallKillSwitch;

    // ================================================================ orders

    public ObservableCollection<OrderPlanChoice> OrderPlans { get; } = new();

    public OrderDraft Draft { get; private set; } = new();

    public OrderPlanChoice? SelectedPlan { get; set; }

    public bool OrdersConfigured => !string.IsNullOrWhiteSpace(_settings.OrdersBaseUrl);

    public string OrderServiceName { get; private set; } = string.Empty;

    public string OrderNotice { get; private set; } = string.Empty;

    public bool HasOrderNotice => !string.IsNullOrWhiteSpace(OrderNotice);

    public bool OrderBusy
    {
        get => _orderBusy;
        private set { _orderBusy = value; Raise(nameof(OrderBusy)); Raise(nameof(OrderCanSubmit)); }
    }

    public bool OrderCanSubmit => !_orderBusy && OrdersConfigured;

    public string OrderCode { get; private set; } = string.Empty;
    public bool OrderPlaced { get; private set; }
    public string OrderError { get; private set; } = string.Empty;
    public bool OrderHasError => !string.IsNullOrWhiteSpace(OrderError);

    public string TrackCode { get; set; } = string.Empty;
    public string TrackAnswer { get; private set; } = string.Empty;
    public bool TrackHasAnswer => !string.IsNullOrWhiteSpace(TrackAnswer);

    public int OrderMonthsIndex
    {
        get => Math.Max(0, Array.IndexOf(MonthChoices, Draft.Months));
        set
        {
            var index = Math.Clamp(value, 0, MonthChoices.Length - 1);
            Draft.Months = MonthChoices[index];
            Raise(nameof(OrderMonthsIndex));
        }
    }

    public int OrderContactKindIndex
    {
        get => Math.Max(0, Array.IndexOf(ContactKinds, Draft.ContactKind));
        set
        {
            var index = Math.Clamp(value, 0, ContactKinds.Length - 1);
            Draft.ContactKind = ContactKinds[index];
            Raise(nameof(OrderContactKindIndex));
        }
    }

    // ============================================================== commands

    public RelayCommand RefreshIpCommand { get; private set; } = null!;
    public RelayCommand CopyIpCommand { get; private set; } = null!;
    public RelayCommand ExportSettingsCommand { get; private set; } = null!;
    public RelayCommand ImportSettingsCommand { get; private set; } = null!;
    public RelayCommand SetupEverythingCommand { get; private set; } = null!;
    public RelayCommand InstallFirewallCommand { get; private set; } = null!;
    public RelayCommand RemoveFirewallCommand { get; private set; } = null!;
    public RelayCommand LoadPricesCommand { get; private set; } = null!;
    public RelayCommand SwitchServiceCommand { get; private set; } = null!;
    public RelayCommand NextServiceCommand { get; private set; } = null!;
    public RelayCommand ActivateCommand { get; private set; } = null!;
    public RelayCommand FindAppsCommand { get; private set; } = null!;
    public RelayCommand BuyPlanCommand { get; private set; } = null!;
    public RelayCommand PlaceOrderCommand { get; private set; } = null!;
    public RelayCommand NewOrderCommand { get; private set; } = null!;
    public RelayCommand TrackOrderCommand { get; private set; } = null!;
    public RelayCommand ReloadOrderServiceCommand { get; private set; } = null!;

    private void BuildFeatureCommands()
    {
        RefreshIpCommand = new RelayCommand(() => Guard.RefreshAddressNow());

        // Settings in and out of a file. What it is for: setting a second
        // machine up the same way, and keeping a copy before a reinstall.
        ExportSettingsCommand = new RelayCommand(() =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = $"safechat-settings-{DateTime.Now:yyyy-MM-dd}.json",
                Filter = "SafeChat settings (*.json)|*.json|All files (*.*)|*.*"
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            try
            {
                File.WriteAllText(dialog.FileName, SettingsStore.Export(_settings));
                _log.Add(ActivityKind.Good, "Settings exported", dialog.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, L["Confirm_Title"], MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        });

        ImportSettingsCommand = new RelayCommand(() =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "SafeChat settings (*.json)|*.json|All files (*.*)|*.*"
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            GuardSettings imported;

            try
            {
                imported = SettingsStore.Import(File.ReadAllText(dialog.FileName), _settings);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, L["Confirm_Title"], MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var confirm = MessageBox.Show(
                L["Set_ImportConfirm"], L["Confirm_Title"],
                MessageBoxButton.OKCancel, MessageBoxImage.Question);

            if (confirm != MessageBoxResult.OK)
            {
                return;
            }

            Edit = imported;
            SaveSettings();
            RaiseAllSettings();
            RefreshServiceTabs();
            ThemeChanged?.Invoke(this, EventArgs.Empty);
            _log.Add(ActivityKind.Good, "Settings imported", dialog.FileName);
        });

        CopyIpCommand = new RelayCommand(() =>
        {
            var info = _snapshot?.Ip;
            if (info is not { Ok: true } || string.IsNullOrWhiteSpace(info.Ip))
            {
                return;
            }

            try
            {
                Clipboard.SetText(info.Ip);
                _ipCopied = true;
                Raise(nameof(IpCopyLabel));

                var reset = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                reset.Tick += (s, _) =>
                {
                    _ipCopied = false;
                    Raise(nameof(IpCopyLabel));
                    ((System.Windows.Threading.DispatcherTimer)s!).Stop();
                };
                reset.Start();
            }
            catch
            {
                // The clipboard can be held by another program; not worth a dialog.
            }
        });

        SetupEverythingCommand = new RelayCommand(() => _ = RunAutoSetupAsync(force: true));

        InstallFirewallCommand = new RelayCommand(() =>
        {
            var path = string.IsNullOrWhiteSpace(_settings.Active.ExecutablePath)
                ? ClaudeLauncher.Detect()
                : _settings.Active.ExecutablePath;

            if (string.IsNullOrWhiteSpace(path))
            {
                MessageBox.Show(L["Fw_NoClaude"], L["Confirm_Title"], MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var (ok, message) = FirewallController.Install(_settings.Active, path);

            if (ok)
            {
                Edit.Active.EnableFirewallKillSwitch = true;
                _settings.Active.EnableFirewallKillSwitch = true;
                _log.Add(ActivityKind.Good, "Firewall kill switch set up", "Switching needs no more approval.");
                Persist();
            }
            else if (!string.IsNullOrWhiteSpace(message))
            {
                MessageBox.Show(message, L["Confirm_Title"], MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            RaiseAllSettings();
            RefreshFeatureText();
        });

        RemoveFirewallCommand = new RelayCommand(() =>
        {
            FirewallController.Uninstall(_settings.Active);
            Edit.Active.EnableFirewallKillSwitch = false;
            _settings.Active.EnableFirewallKillSwitch = false;
            _log.Add(ActivityKind.Info, "Firewall kill switch removed");
            Persist();
            RaiseAllSettings();
            RefreshFeatureText();
        });


        LoadPricesCommand = new RelayCommand(() => _ = LoadPricesAsync());

        SwitchServiceCommand = new RelayCommand(p => SwitchService(p as string));
        NextServiceCommand = new RelayCommand(NextService);

        ActivateCommand = new RelayCommand(() => _ = ActivateAsync());

        // Looks for every watched app again, for when one was installed or moved
        // after SafeChat was.
        FindAppsCommand = new RelayCommand(() => _ = RunAutoSetupAsync(force: true));

        BuyPlanCommand = new RelayCommand(p =>
        {
            // Straight from a price card into the order form with the plan set,
            // because making someone pick the plan twice is how orders get lost.
            if (p is PricedPlan plan)
            {
                Draft = new OrderDraft { ContactKind = "telegram", Months = 1, Plan = plan.Key };
                OrderPlaced = false;
                OrderCode = string.Empty;
                OrderError = string.Empty;

                var choice = OrderPlans.FirstOrDefault(
                    c => string.Equals(c.Key, plan.Key, StringComparison.OrdinalIgnoreCase));

                if (choice is not null)
                {
                    SelectedPlan = choice;
                }

                Raise(nameof(Draft));
                Raise(nameof(SelectedPlan));
                Raise(nameof(OrderPlaced));
                Raise(nameof(OrderCode));
                Raise(nameof(OrderError));
                Raise(nameof(OrderHasError));
                Raise(nameof(OrderMonthsIndex));
                Raise(nameof(OrderContactKindIndex));
            }

            Page = AppPage.Orders;
        });

        PlaceOrderCommand = new RelayCommand(() => _ = PlaceOrderAsync());

        NewOrderCommand = new RelayCommand(() =>
        {
            Draft = new OrderDraft
            {
                ContactKind = "telegram",
                Months = 1,
                Service = _settings.ActiveServiceKey
            };
            OrderPlaced = false;
            OrderCode = string.Empty;
            OrderError = string.Empty;
            Raise(nameof(Draft));
            Raise(nameof(OrderPlaced));
            Raise(nameof(OrderCode));
            Raise(nameof(OrderError));
            Raise(nameof(OrderHasError));
            Raise(nameof(OrderMonthsIndex));
            Raise(nameof(OrderContactKindIndex));
        });

        TrackOrderCommand = new RelayCommand(() => _ = TrackOrderAsync());

        ReloadOrderServiceCommand = new RelayCommand(() => _ = LoadOrderServiceAsync());
    }

    // =============================================================== usage



    private static string Compact(long value)
    {
        if (value >= 1_000_000_000) return (value / 1_000_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "B";
        if (value >= 1_000_000) return (value / 1_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "M";
        if (value >= 1_000) return (value / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "K";
        return value.ToString(CultureInfo.InvariantCulture);
    }

    // ================================================================== buy

    public ObservableCollection<PricedPlan> Prices { get; } = new();

    public bool PricesLoading { get; private set; }
    public bool PricesReady { get; private set; }
    public bool PricesFromCache { get; private set; }
    public bool PricesStale { get; private set; }
    public DateTimeOffset? PricesUpdatedAt { get; private set; }

    public bool ShowPriceCards => PricesReady && Prices.Count > 0;

    public bool ShowPriceEmpty => !PricesLoading && !ShowPriceCards;

    /// <summary>Why the buy page has nothing on it, in the user's language.</summary>
    public string PriceEmptyText
    {
        get
        {
            if (!OrdersConfigured)
            {
                return L["Buy_NoServer"];
            }

            return PricesReady ? L["Buy_NoRate"] : L["Buy_Unreachable"];
        }
    }

    /// <summary>The small print under the cards: how old these numbers are.</summary>
    public string PriceFootnote
    {
        get
        {
            if (!ShowPriceCards)
            {
                return string.Empty;
            }

            if (PricesFromCache)
            {
                return L["Buy_Cached"];
            }

            if (PricesStale)
            {
                return L["Buy_Stale"];
            }

            if (PricesUpdatedAt is { } at)
            {
                return L["Buy_Updated"] + " " + at.ToLocalTime().ToString("yyyy/MM/dd HH:mm");
            }

            return string.Empty;
        }
    }

    public async Task LoadPricesAsync()
    {
        PricesLoading = true;
        RaisePrices();

        var list = await _pricing.LoadAsync(_settings.OrdersBaseUrl).ConfigureAwait(true);

        Prices.Clear();

        // A plan with no price is not a price card. The API-credit row belongs on
        // the order form, where the customer says how much they want.
        foreach (var plan in list.Plans.Where(p => !p.Variable && p.Toman > 0))
        {
            Prices.Add(plan);
        }

        PricesReady = list.Ok && list.RateReady;
        PricesFromCache = list.FromCache;
        PricesStale = list.RateStale;
        PricesUpdatedAt = list.UpdatedAt;
        PricesLoading = false;

        RaisePrices();
    }

    private void RaisePrices()
    {
        Raise(nameof(PricesLoading));
        Raise(nameof(PricesReady));
        Raise(nameof(PricesFromCache));
        Raise(nameof(PricesStale));
        Raise(nameof(ShowPriceCards));
        Raise(nameof(ShowPriceEmpty));
        Raise(nameof(PriceEmptyText));
        Raise(nameof(PriceFootnote));
    }

    // ============================================================== orders

    public async Task LoadOrderServiceAsync()
    {
        OrderPlans.Clear();
        OrderServiceName = string.Empty;
        OrderNotice = string.Empty;

        if (!OrdersConfigured)
        {
            RaiseOrderService();
            return;
        }

        var service = await _orders.GetServiceAsync(_settings.OrdersBaseUrl).ConfigureAwait(true);

        if (service is not null)
        {
            OrderServiceName = service.BusinessName;
            OrderNotice = service.Notice;

            // Only the plans for the service being shown. Ordering ChatGPT and
            // being offered Claude Max is the sort of thing that gets refunded.
            foreach (var plan in service.Plans.Where(p => p.BelongsTo(_settings.ActiveServiceKey)))
            {
                OrderPlans.Add(new OrderPlanChoice
                {
                    Key = plan.Key,
                    Text = plan.Display(L.IsRightToLeft)
                });
            }

            SelectedPlan = OrderPlans.FirstOrDefault();
            Raise(nameof(SelectedPlan));
        }

        RaiseOrderService();
    }

    private void RaiseOrderService()
    {
        Raise(nameof(OrderServiceName));
        Raise(nameof(OrderNotice));
        Raise(nameof(HasOrderNotice));
        Raise(nameof(OrdersConfigured));
        Raise(nameof(OrderCanSubmit));
    }

    private async Task PlaceOrderAsync()
    {
        OrderError = string.Empty;
        Raise(nameof(OrderError));
        Raise(nameof(OrderHasError));

        if (SelectedPlan is null)
        {
            OrderError = L["Ord_Err_unknown_plan"];
            Raise(nameof(OrderError));
            Raise(nameof(OrderHasError));
            return;
        }

        Draft.Plan = SelectedPlan.Key;
        Draft.Service = _settings.ActiveServiceKey;
        OrderBusy = true;

        try
        {
            var result = await _orders.PlaceAsync(_settings.OrdersBaseUrl, Draft).ConfigureAwait(true);

            if (result.Ok)
            {
                OrderCode = result.Code;
                OrderPlaced = true;
                _log.Add(ActivityKind.Good, "Order placed", result.Code);
                Raise(nameof(OrderCode));
                Raise(nameof(OrderPlaced));
            }
            else
            {
                var known = L["Ord_Err_" + result.Error];
                OrderError = known == "Ord_Err_" + result.Error ? L["Ord_Err_failed"] : known;
            }
        }
        catch (Exception ex)
        {
            OrderError = ex.Message;
        }
        finally
        {
            OrderBusy = false;
            Raise(nameof(OrderError));
            Raise(nameof(OrderHasError));
        }
    }

    private async Task TrackOrderAsync()
    {
        TrackAnswer = string.Empty;
        Raise(nameof(TrackAnswer));
        Raise(nameof(TrackHasAnswer));

        if (!OrdersConfigured || string.IsNullOrWhiteSpace(TrackCode))
        {
            return;
        }

        var view = await _orders.TrackAsync(_settings.OrdersBaseUrl, TrackCode).ConfigureAwait(true);

        if (view is null)
        {
            TrackAnswer = L["Ord_Err_not_found"];
        }
        else
        {
            var status = L["Ord_St_" + view.Status];
            if (status == "Ord_St_" + view.Status)
            {
                status = view.Status;
            }

            TrackAnswer = string.IsNullOrWhiteSpace(view.Message)
                ? $"{view.Code} · {view.Plan} · {status}"
                : $"{view.Code} · {view.Plan} · {status}\n{view.Message}";
        }

        Raise(nameof(TrackAnswer));
        Raise(nameof(TrackHasAnswer));
    }

    // ============================================================== refresh

    private void RefreshFeatureText()
    {
        Raise(nameof(IpValue));
        Raise(nameof(IpWhere));
        Raise(nameof(IpNetwork));
        Raise(nameof(IpSource));
        Raise(nameof(IpBrush));
        Raise(nameof(ShowIpLeak));
        Raise(nameof(ShowIpCard));
        Raise(nameof(KnownHomeIp));
        Raise(nameof(FirewallReady));
        Raise(nameof(FirewallStatus));
        Raise(nameof(FirewallBrush));
        Raise(nameof(ShowFirewallChip));
        Raise(nameof(OrdersConfigured));
        Raise(nameof(OrderCanSubmit));
        Raise(nameof(SetupStatus));
        Raise(nameof(SetupBrush));
        Raise(nameof(ClaudeFoundVia));
        Raise(nameof(GraceActive));
        Raise(nameof(GraceEndsAt));
        Raise(nameof(GraceTitle));
        Raise(nameof(GraceBody));
        Raise(nameof(GraceResolvedText));
        Raise(nameof(GraceTotalSeconds));
    }
}
