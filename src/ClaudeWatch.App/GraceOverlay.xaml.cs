using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ClaudeWatch.App;

/// <summary>
/// The on-top countdown that appears the moment Claude's traffic is cut. It
/// never takes focus, because the whole point of the ten seconds is that you
/// can go and click your VPN client.
///
/// It is also careful about how it sits on top. A top-most window that appears
/// over a full-screen app and is then destroyed can leave the shell believing
/// the full-screen app still owns the top of the z-order, which shows up as
/// taskbar clicks opening windows *behind* it, and outlives this app. So the
/// window never activates, never appears in alt-tab, and steps down out of the
/// top-most band before it closes.
/// </summary>
public partial class GraceOverlay : Window
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndNoTopmost = new(-2);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    private readonly MainViewModel _model;
    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromMilliseconds(100) };

    private bool _resolved;
    private bool _steppedDown;
    private DateTimeOffset? _closeAt;

    public GraceOverlay(MainViewModel model)
    {
        _model = model;
        InitializeComponent();
        DataContext = model;

        _ticker.Tick += OnTick;

        SourceInitialized += (_, _) => MarkAsOverlay();

        Loaded += (_, _) =>
        {
            PlaceAtTopCentre();
            _ticker.Start();
        };
    }

    /// <summary>
    /// Tells Windows this is furniture rather than a window someone switches to:
    /// it cannot be activated, and it is not in alt-tab. Without WS_EX_NOACTIVATE
    /// a click on the banner pulls focus away from whatever is full-screen, which
    /// is the moment the z-order gets stuck.
    /// </summary>
    private void MarkAsOverlay()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var style = GetWindowLong(handle, GwlExStyle);
            SetWindowLong(handle, GwlExStyle, style | WsExToolWindow | WsExNoActivate);
        }
        catch
        {
            // Cosmetic. A warning that shows without this is still a warning.
        }
    }

    /// <summary>
    /// Leaves the top-most band before the window is destroyed, and gives the
    /// shell a moment with the banner still alive but ordinary. Destroying a
    /// top-most window over a full-screen app is what leaves the taskbar unable
    /// to raise anything above it.
    /// </summary>
    private void StepDownFromTop()
    {
        if (_steppedDown)
        {
            return;
        }

        _steppedDown = true;

        try
        {
            Topmost = false;

            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                SetWindowPos(handle, HwndNoTopmost, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
            }
        }
        catch
        {
        }
    }

    /// <summary>Top centre of the work area, clear of the taskbar.</summary>
    private void PlaceAtTopCentre()
    {
        try
        {
            var width = SystemParameters.WorkArea.Width;
            Left = SystemParameters.WorkArea.Left + Math.Max(0, (width - ActualWidth) / 2);
            Top = SystemParameters.WorkArea.Top + 24;
        }
        catch
        {
            Left = 80;
            Top = 40;
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // The countdown has finished one way or the other.
        if (_resolved)
        {
            if (_closeAt is { } closeAt && DateTimeOffset.Now >= closeAt)
            {
                CloseOverlay();
            }

            return;
        }

        if (!_model.GraceActive)
        {
            Resolve();
            return;
        }

        var endsAt = _model.GraceEndsAt;
        if (endsAt is null)
        {
            return;
        }

        var remaining = endsAt.Value - DateTimeOffset.Now;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        Countdown.Text = Math.Ceiling(remaining.TotalSeconds).ToString("0");

        var total = Math.Max(1, _model.GraceTotalSeconds);
        var fraction = Math.Clamp(remaining.TotalSeconds / total, 0, 1);
        BarFill.Width = Math.Max(0, BarTrack.ActualWidth * fraction);
    }

    /// <summary>
    /// Grace ended. If it ended because the tunnel came back, say so for a
    /// moment; if Claude was closed, just get out of the way.
    /// </summary>
    private void Resolve()
    {
        _resolved = true;

        if (_model.CanOpenClaude)
        {
            Headline.Text = _model.GraceResolvedText;
            Headline.Foreground = MainViewModel.Palette("Good");
            Body.Visibility = Visibility.Collapsed;
            BarTrack.Visibility = Visibility.Collapsed;
            Countdown.Visibility = Visibility.Collapsed;
            Caption.Visibility = Visibility.Collapsed;
            Ring.Stroke = MainViewModel.Palette("Good");
            Tick.Visibility = Visibility.Visible;
            StopNow.Visibility = Visibility.Collapsed;
            OpenApp.Visibility = Visibility.Collapsed;

            _closeAt = DateTimeOffset.Now.AddSeconds(2.5);
            return;
        }

        CloseOverlay();
    }

    private void CloseOverlay()
    {
        _ticker.Stop();
        StepDownFromTop();

        // A moment as an ordinary window, so the shell re-sorts the z-order
        // while this thing still exists rather than after it is gone.
        var closer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };

        closer.Tick += (sender, _) =>
        {
            ((DispatcherTimer)sender!).Stop();

            try
            {
                Close();
            }
            catch
            {
                // Already closing.
            }
        };

        closer.Start();
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
            // DragMove throws if the button was released first.
        }
    }

    private void OnOpenApp(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
        {
            app.ShowWindow();
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Covers the paths that do not go through CloseOverlay, such as the app
        // shutting down with the banner still up.
        StepDownFromTop();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _ticker.Stop();
        _ticker.Tick -= OnTick;
        base.OnClosed(e);
    }
}
