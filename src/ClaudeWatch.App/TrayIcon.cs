using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using ClaudeWatch.Core;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace ClaudeWatch.App;

/// <summary>
/// The tray presence: a dot that carries the current state as a colour, a menu
/// with the three actions worth reaching without opening the window, and balloon
/// notifications when something changes.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    private readonly MainViewModel _model;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _openItem;
    private readonly Forms.ToolStripMenuItem _stopItem;
    private readonly Forms.ToolStripMenuItem _pauseItem;
    private readonly Forms.ToolStripMenuItem _resumeItem;
    private readonly Forms.ToolStripMenuItem _exitItem;
    private readonly Dictionary<string, Drawing.Icon> _cache = new();

    private GuardPhase _lastPhase = (GuardPhase)(-1);

    public TrayIcon(MainViewModel model)
    {
        _model = model;

        _openItem = new Forms.ToolStripMenuItem("Open");
        _openItem.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);

        _stopItem = new Forms.ToolStripMenuItem("Stop Claude");
        _stopItem.Click += (_, _) => _model.RequestStopFromTray();

        _pauseItem = new Forms.ToolStripMenuItem("Pause");
        _pauseItem.Click += (_, _) => _model.Guard.Pause(TimeSpan.FromMinutes(30));

        _resumeItem = new Forms.ToolStripMenuItem("Resume");
        _resumeItem.Click += (_, _) => _model.Guard.Resume();

        _exitItem = new Forms.ToolStripMenuItem("Quit");
        _exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_openItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_stopItem);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(_resumeItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_exitItem);
        menu.Opening += OnMenuOpening;

        _icon = new Forms.NotifyIcon
        {
            Visible = true,
            Text = "SafeChat",
            ContextMenuStrip = menu,
            Icon = BuildIcon(GuardPhase.Off)
        };

        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        _icon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);

        _model.PropertyChanged += OnModelChanged;
    }

    public event EventHandler? OpenRequested;

    public event EventHandler? ExitRequested;

    private void OnMenuOpening(object? sender, CancelEventArgs e)
    {
        _openItem.Text = _model.L["Tray_Open"];
        _stopItem.Text = _model.L["Tray_Stop"];
        _pauseItem.Text = _model.L["Tray_Pause"];
        _resumeItem.Text = _model.L["Tray_Resume"];
        _exitItem.Text = _model.L["Tray_Exit"];

        _stopItem.Enabled = _model.CanStopClaude;
        _resumeItem.Visible = _model.ShowPauseBanner;
        _pauseItem.Visible = !_model.ShowPauseBanner;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(MainViewModel.StatusTitle) or nameof(MainViewModel.VpnValue)))
        {
            return;
        }

        var phase = _model.CurrentPhase;
        if (phase != _lastPhase)
        {
            _lastPhase = phase;
            _icon.Icon = BuildIcon(phase);
        }

        var text = $"Claude Watch — {_model.StatusTitle}";
        _icon.Text = text.Length > 62 ? text[..62] : text;
    }

    public void Notify(NotificationRequest request)
    {
        try
        {
            var icon = request.Kind switch
            {
                ActivityKind.Alert => Forms.ToolTipIcon.Error,
                ActivityKind.Warning => Forms.ToolTipIcon.Warning,
                _ => Forms.ToolTipIcon.Info
            };

            _icon.ShowBalloonTip(
                5000,
                string.IsNullOrWhiteSpace(request.Title) ? "SafeChat" : request.Title,
                string.IsNullOrWhiteSpace(request.Message) ? " " : request.Message,
                icon);
        }
        catch
        {
        }
    }

    private Drawing.Icon BuildIcon(GuardPhase phase)
    {
        var key = phase.ToString();
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var colour = phase switch
        {
            GuardPhase.Ready => Drawing.Color.FromArgb(0x5F, 0xA9, 0x7C),
            GuardPhase.Blocked => Drawing.Color.FromArgb(0xD2, 0x61, 0x5A),
            GuardPhase.Mismatch => Drawing.Color.FromArgb(0xD9, 0xA1, 0x5B),
            GuardPhase.Paused => Drawing.Color.FromArgb(0xD9, 0xA1, 0x5B),
            GuardPhase.Standby => Drawing.Color.FromArgb(0x7C, 0x9C, 0xC9),
            _ => Drawing.Color.FromArgb(0x8B, 0x86, 0x80)
        };

        using var bitmap = new Drawing.Bitmap(32, 32);
        using (var canvas = Drawing.Graphics.FromImage(bitmap))
        {
            canvas.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            canvas.Clear(Drawing.Color.Transparent);

            using var ring = new Drawing.Pen(Drawing.Color.FromArgb(150, 0, 0, 0), 2f);
            using var fill = new Drawing.SolidBrush(colour);

            canvas.FillEllipse(fill, 4, 4, 24, 24);
            canvas.DrawEllipse(ring, 4, 4, 24, 24);

            if (phase == GuardPhase.Paused)
            {
                using var bar = new Drawing.SolidBrush(Drawing.Color.FromArgb(230, 26, 21, 18));
                canvas.FillRectangle(bar, 12, 11, 3, 10);
                canvas.FillRectangle(bar, 17, 11, 3, 10);
            }
        }

        var handle = bitmap.GetHicon();
        try
        {
            var icon = (Drawing.Icon)Drawing.Icon.FromHandle(handle).Clone();
            _cache[key] = icon;
            return icon;
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    public void Dispose()
    {
        try
        {
            _model.PropertyChanged -= OnModelChanged;
            _icon.Visible = false;
            _icon.Dispose();

            foreach (var icon in _cache.Values)
            {
                icon.Dispose();
            }

            _cache.Clear();
        }
        catch
        {
        }
    }
}
