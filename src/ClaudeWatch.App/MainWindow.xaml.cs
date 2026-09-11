using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClaudeWatch.App;

public partial class MainWindow : Window
{
    private const int WmHotKey = 0x0312;
    private const int HotkeyId = 0xB0B0;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint VkK = 0x4B;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private bool _trayHintShown;
    private bool _hotkeyRegistered;

    public MainWindow()
    {
        InitializeComponent();
        StateChanged += OnStateChanged;
        SourceInitialized += OnSourceInitialized;
    }

    private MainViewModel? Model => DataContext as MainViewModel;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        var source = HwndSource.FromHwnd(helper.Handle);
        source?.AddHook(HandleMessage);

        if (Model?.Live.EnableKillHotkey == true)
        {
            try
            {
                _hotkeyRegistered = RegisterHotKey(helper.Handle, HotkeyId, ModControl | ModAlt, VkK);
            }
            catch
            {
                _hotkeyRegistered = false;
            }
        }
    }

    private IntPtr HandleMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotKey && wParam.ToInt32() == HotkeyId)
        {
            Model?.RequestStopFromTray();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        // A maximized borderless window overhangs the work area by the resize
        // border, so pull the content back in by exactly that much.
        if (WindowState == WindowState.Maximized)
        {
            var scale = 1.0;
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget is not null)
            {
                scale = source.CompositionTarget.TransformToDevice.M11;
                if (scale <= 0)
                {
                    scale = 1.0;
                }
            }

            RootShell.Margin = new Thickness(8 / scale);
        }
        else
        {
            RootShell.Margin = new Thickness(0);
        }

        if (WindowState == WindowState.Minimized && Model?.Live.MinimizeToTray == true)
        {
            Hide();
            WindowState = WindowState.Normal;
            ShowTrayHint();
        }
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!App.Exiting && Model?.Live.CloseToTray == true)
        {
            e.Cancel = true;
            Hide();
            ShowTrayHint();
            return;
        }

        if (_hotkeyRegistered)
        {
            try
            {
                UnregisterHotKey(new WindowInteropHelper(this).Handle, HotkeyId);
            }
            catch
            {
            }
        }

        base.OnClosing(e);

        if (!App.Exiting && Application.Current is App app)
        {
            app.ExitApp();
        }
    }

    private void ShowTrayHint()
    {
        if (_trayHintShown || Model is null)
        {
            return;
        }

        _trayHintShown = true;

        if (Model.Live.ShowNotifications)
        {
            Model.NotifyFromWindow(Model.L["App_Name"], Model.L["Tray_Running"]);
        }
    }
}
