using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace ClaudeWatch.App;

/// <summary>
/// The on-top countdown that appears the moment Claude's traffic is cut. It
/// never takes focus, because the whole point of the ten seconds is that you
/// can go and click your VPN client.
/// </summary>
public partial class GraceOverlay : Window
{
    private readonly MainViewModel _model;
    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromMilliseconds(100) };

    private bool _resolved;
    private DateTimeOffset? _closeAt;

    public GraceOverlay(MainViewModel model)
    {
        _model = model;
        InitializeComponent();
        DataContext = model;

        _ticker.Tick += OnTick;
        Loaded += (_, _) =>
        {
            PlaceAtTopCentre();
            _ticker.Start();
        };
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

        try
        {
            Close();
        }
        catch
        {
            // Already closing.
        }
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

    protected override void OnClosed(EventArgs e)
    {
        _ticker.Stop();
        _ticker.Tick -= OnTick;
        base.OnClosed(e);
    }
}
