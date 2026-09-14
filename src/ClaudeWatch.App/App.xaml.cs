using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using ClaudeWatch.Core;

namespace ClaudeWatch.App;

public partial class App : Application
{
    private const string InstanceName = "ClaudeWatch.SingleInstance.9F1C";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showSignal;
    private MainViewModel? _viewModel;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private GraceOverlay? _overlay;
    private System.Windows.Threading.DispatcherTimer? _licenceTimer;

    public static MainViewModel? Model { get; private set; }

    /// <summary>Set once the user really means to quit, so the window stops hiding itself.</summary>
    public static bool Exiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The uninstaller calls this before it deletes the folder. The firewall
        // rule, the scheduled tasks and the start-with-Windows entry all live
        // outside it and would otherwise be left behind, with the block still
        // on and nothing left to lift it.
        if (e.Args.Any(a => a.Equals("--uninstall-cleanup", StringComparison.OrdinalIgnoreCase)))
        {
            RemoveEverythingOutsideTheFolder();
            Shutdown();
            return;
        }

        _instanceMutex = new Mutex(true, InstanceName, out var isFirst);
        if (!isFirst)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(InstanceName + ".Show", out var existing))
                {
                    existing.Set();
                    existing.Dispose();
                }
            }
            catch
            {
            }

            Shutdown();
            return;
        }

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show");
        StartShowListener();

        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrash(args.Exception);
            MessageBox.Show(
                "SafeChat hit an unexpected error and kept running.\n\n" + args.Exception.Message,
                "SafeChat", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        _viewModel = new MainViewModel();
        Model = _viewModel;

        // The accent follows the selected service, so switching repaints the window.
        ApplyTheme(_viewModel.Live.Theme, _viewModel.EffectiveAccent);
        _viewModel.ThemeChanged += (_, _) => ApplyTheme(_viewModel.Live.Theme, _viewModel.EffectiveAccent);
        _viewModel.RefreshServiceTabs();

        _tray = new TrayIcon(_viewModel);
        _tray.OpenRequested += (_, _) => ShowWindow();
        _tray.ExitRequested += (_, _) => ExitApp();

        _window = new MainWindow { DataContext = _viewModel };
        _viewModel.NotificationRequested += (_, request) => _tray?.Notify(request);
        _viewModel.PropertyChanged += OnModelChanged;

        var startHidden = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase))
                          || _viewModel.Live.StartMinimized;

        // The wizard comes first on a fresh install, and its answers decide what
        // the automatic setup then goes looking for.
        if (_viewModel.NeedsSetup && !startHidden)
        {
            RunWizard();
        }
        else
        {
            if (!startHidden)
            {
                _window.Show();
            }

            _ = _viewModel.RunAutoSetupAsync();
        }

        _viewModel.Start();
        _ = _viewModel.StartLicenceAsync();
        StartLicenceWatch();
        _viewModel.StartUpdateWatch();
    }

    /// <summary>
    /// The countdown banner only ever needs to be raised here. It takes itself
    /// down, so that when the tunnel comes back it can show the green "Claude
    /// stays open" line for a beat before disappearing.
    /// </summary>
    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.GraceActive) || _viewModel is null || Exiting)
        {
            return;
        }

        if (!_viewModel.GraceActive || !_viewModel.Live.ShowGraceOverlay || _overlay is not null)
        {
            return;
        }

        try
        {
            var overlay = new GraceOverlay(_viewModel);
            overlay.Closed += (_, _) => _overlay = null;
            _overlay = overlay;
            overlay.Show();
        }
        catch (Exception error)
        {
            // A warning that cannot open must never take the guard down with it.
            _overlay = null;
            WriteCrash(error);
        }
    }

    /// <summary>
    /// The first-run questions. If anything about it fails, the app carries on
    /// without it rather than leaving someone with a window they cannot open.
    /// </summary>
    private void RunWizard()
    {
        if (_viewModel is null || _window is null)
        {
            return;
        }

        try
        {
            var wizard = new SetupWizard(_viewModel);
            wizard.ShowDialog();
        }
        catch (Exception error)
        {
            WriteCrash(error);
            _viewModel.CompleteSetup();
        }

        _window.Show();
    }

    /// <summary>
    /// Re-confirms the key with the server every six hours. Nothing happens on
    /// a failure: an unreachable server must never switch protection off.
    /// </summary>
    private void StartLicenceWatch()
    {
        _licenceTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromHours(6)
        };

        _licenceTimer.Tick += (_, _) => _ = _viewModel?.RecheckLicenceAsync();
        _licenceTimer.Start();
    }

    private void StartShowListener()
    {
        var handle = _showSignal;
        if (handle is null)
        {
            return;
        }

        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    handle.WaitOne();
                    Dispatcher.Invoke(ShowWindow);
                }
                catch
                {
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "ClaudeWatch.ShowListener"
        };

        thread.Start();
    }

    public void ShowWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();

        // No top-most flick to force the window forward. It works, and it also
        // reorders the top-most band underneath whatever is full-screen at the
        // time, which is how a full-screen app ends up stuck above everything.
        // A taskbar flash when Windows refuses the foreground is the cheaper
        // failure.
    }

    public void ExitApp()
    {
        Exiting = true;

        try
        {
            _overlay?.Close();
        }
        catch
        {
        }

        _licenceTimer?.Stop();
        _viewModel?.Shutdown();
        _tray?.Dispose();
        Shutdown();
    }

    public void ApplyTheme(AppTheme theme, string accentHex)
    {
        var resolved = theme switch
        {
            AppTheme.Light => "Light",
            AppTheme.Dark => "Dark",
            _ => WindowsPrefersLight() ? "Light" : "Dark"
        };

        // Relative pack URIs usually resolve; the component form is the fallback.
        var candidates = new[]
        {
            new Uri($"Theme/{resolved}.xaml", UriKind.Relative),
            new Uri($"pack://application:,,,/SafeChat;component/Theme/{resolved}.xaml", UriKind.Absolute)
        };

        foreach (var candidate in candidates)
        {
            try
            {
                var dictionary = new ResourceDictionary { Source = candidate };

                if (Resources.MergedDictionaries.Count > 0)
                {
                    Resources.MergedDictionaries[0] = dictionary;
                }
                else
                {
                    Resources.MergedDictionaries.Add(dictionary);
                }

                break;
            }
            catch
            {
                // Try the next form.
            }
        }

        ApplyAccent(accentHex, resolved == "Light");
    }

    private void ApplyAccent(string accentHex, bool light)
    {
        Color accent;

        try
        {
            accent = (Color)ColorConverter.ConvertFromString(accentHex);
        }
        catch
        {
            accent = Color.FromRgb(0xD9, 0x77, 0x57);
        }

        Resources["Accent"] = new SolidColorBrush(accent);
        Resources["AccentSoft"] = new SolidColorBrush(accent) { Opacity = light ? 0.14 : 0.18 };
        Resources["AccentHover"] = new SolidColorBrush(accent) { Opacity = 0.85 };

        // Pick readable text for buttons filled with the accent.
        var luminance = (0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B) / 255.0;
        Resources["OnAccent"] = new SolidColorBrush(luminance > 0.5
            ? Color.FromRgb(0x1A, 0x15, 0x12)
            : Color.FromRgb(0xFF, 0xFF, 0xFF));
    }

    private static bool WindowsPrefersLight()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 1;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteCrash(Exception exception)
    {
        try
        {
            var path = Path.Combine(AppPaths.Root, "error.log");
            File.AppendAllText(path, $"{DateTimeOffset.Now:O}  {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    /// <summary>
    /// Everything the app put on the machine that is not inside its own folder.
    /// Run from the uninstaller, which is already elevated, so none of this
    /// asks for approval a second time. Each step is on its own: a task that
    /// was never created is not a reason to leave the firewall rule behind.
    /// </summary>
    private static void RemoveEverythingOutsideTheFolder()
    {
        var settings = new SettingsStore().Load();

        foreach (var profile in settings.Services)
        {
            try
            {
                FirewallController.Set(profile, blocked: false);
            }
            catch
            {
            }

            try
            {
                FirewallController.Uninstall(profile);
            }
            catch
            {
            }
        }

        try
        {
            PrivilegedHelper.Uninstall();
        }
        catch
        {
        }

        try
        {
            StartupRegistration.Set(false, false);
        }
        catch
        {
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _viewModel?.StopUpdateWatch();
        _tray?.Dispose();
        _showSignal?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
