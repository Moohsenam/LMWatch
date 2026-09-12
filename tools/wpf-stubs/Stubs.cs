// Minimal stand-ins for the WPF, WinForms and System.Drawing surface Claude Watch
// touches. They exist only so the app's C# can be type-checked on a machine that
// cannot restore the Windows Desktop reference pack. They are never compiled into
// the app itself — see tools/check_csharp.sh.
#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;

namespace System.Windows
{
    public enum FlowDirection { LeftToRight, RightToLeft }

    public enum Visibility { Visible, Hidden, Collapsed }

    public enum WindowState { Normal, Minimized, Maximized }

    public enum MessageBoxButton { OK, OKCancel, YesNoCancel, YesNo }

    public enum MessageBoxImage { None, Warning, Question, Information, Error }

    public enum MessageBoxResult { None, OK, Cancel, Yes, No }

    public struct Thickness
    {
        public Thickness(double uniform) { Left = Top = Right = Bottom = uniform; }
        public double Left, Top, Right, Bottom;
    }

    public static class Clipboard
    {
        public static void SetText(string text) { }
        public static string GetText() => string.Empty;
    }

    public static class MessageBox
    {
        public static MessageBoxResult Show(string text) => MessageBoxResult.OK;
        public static MessageBoxResult Show(string text, string caption) => MessageBoxResult.OK;
        public static MessageBoxResult Show(string text, string caption, MessageBoxButton button) => MessageBoxResult.OK;
        public static MessageBoxResult Show(string text, string caption, MessageBoxButton button, MessageBoxImage image) => MessageBoxResult.OK;
    }

    public class ResourceDictionary : IDictionary
    {
        public Uri? Source { get; set; }
        public Collection<ResourceDictionary> MergedDictionaries { get; } = new();
        private readonly Hashtable _items = new();

        public object? this[object key] { get => _items[key]; set => _items[key] = value; }
        public bool Contains(object key) => _items.ContainsKey(key);
        public void Add(object key, object? value) => _items[key] = value;
        public void Clear() => _items.Clear();
        public void Remove(object key) => _items.Remove(key);
        public int Count => _items.Count;
        public bool IsFixedSize => false;
        public bool IsReadOnly => false;
        public ICollection Keys => _items.Keys;
        public ICollection Values => _items.Values;
        public bool IsSynchronized => false;
        public object SyncRoot => this;
        public void CopyTo(Array array, int index) { }
        public IDictionaryEnumerator GetEnumerator() => _items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
    }

    public struct Rect
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }

    public static class SystemParameters
    {
        public static Rect WorkArea => new() { Left = 0, Top = 0, Width = 1920, Height = 1040 };
    }

    public class DependencyObject { }

    public class Visual : DependencyObject { }

    public class UIElement : Visual
    {
        public Visibility Visibility { get; set; }
        public double Opacity { get; set; } = 1;
        public double ActualWidth { get; }
        public double ActualHeight { get; }
    }

    public delegate void RoutedEventHandler(object sender, RoutedEventArgs e);

    public class FrameworkElement : UIElement
    {
        public object? DataContext { get; set; }
        public Thickness Margin { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public event RoutedEventHandler? Loaded;
        public object? TryFindResource(object key) => null;
        public void RaiseLoadedForStubs() => Loaded?.Invoke(this, new RoutedEventArgs());
    }

    public class Control : FrameworkElement { }

    public class ContentControl : Control
    {
        public object? Content { get; set; }
    }

    public class Window : ContentControl
    {
        public string Title { get; set; } = string.Empty;
        public WindowState WindowState { get; set; }
        public bool Topmost { get; set; }
        public bool ShowActivated { get; set; } = true;
        public double Left { get; set; }
        public double Top { get; set; }
        public event EventHandler? StateChanged;
        public event EventHandler? SourceInitialized;
        public event EventHandler? Closed;
        public void Show() { StateChanged?.Invoke(this, EventArgs.Empty); SourceInitialized?.Invoke(this, EventArgs.Empty); }
        public void Hide() { }
        public void Close() { Closed?.Invoke(this, EventArgs.Empty); OnClosed(EventArgs.Empty); }
        public bool Activate() => true;
        public void DragMove() { }
        public bool? DialogResult { get; set; }
        public bool? ShowDialog() => true;
        public System.Windows.Window? Owner { get; set; }
        protected virtual void OnClosing(CancelEventArgs e) { }
        protected virtual void OnClosed(EventArgs e) { }
        public void InitializeComponent() { }
    }

    public class StartupEventArgs : EventArgs
    {
        public string[] Args { get; } = Array.Empty<string>();
    }

    public class ExitEventArgs : EventArgs
    {
        public int ApplicationExitCode { get; set; }
    }

    public class RoutedEventArgs : EventArgs { }

    public class DispatcherUnhandledExceptionEventArgs : EventArgs
    {
        public Exception Exception { get; } = new();
        public bool Handled { get; set; }
    }

    public class Application
    {
        public static Application? Current { get; set; } = new();
        public ResourceDictionary Resources { get; } = new();
        public System.Windows.Threading.Dispatcher Dispatcher { get; } = new();
        public event EventHandler<DispatcherUnhandledExceptionEventArgs>? DispatcherUnhandledException;
        public void Shutdown() { }
        public object? TryFindResource(object key) => Resources[key];
        protected virtual void OnStartup(StartupEventArgs e) { }
        protected virtual void OnExit(ExitEventArgs e) { }
        public void RaiseForStubs() => DispatcherUnhandledException?.Invoke(this, new DispatcherUnhandledExceptionEventArgs());
    }

    public abstract class PresentationSource
    {
        public static PresentationSource? FromVisual(Visual visual) => null;
        public CompositionTargetStub? CompositionTarget { get; }
    }

    public class CompositionTargetStub
    {
        public MatrixStub TransformToDevice { get; }
    }

    public struct MatrixStub
    {
        public double M11 { get; set; }
    }
}

namespace System.Windows.Controls
{
    public class Border : System.Windows.FrameworkElement
    {
        public System.Windows.Media.Brush? Background { get; set; }
        public System.Windows.Media.Brush? BorderBrush { get; set; }
    }

    public class TextBlock : System.Windows.FrameworkElement
    {
        public string Text { get; set; } = string.Empty;
        public System.Windows.Media.Brush? Foreground { get; set; }
    }

    public class Button : System.Windows.ContentControl { }

    public class StackPanel : System.Windows.FrameworkElement { }

    public class CheckBox : System.Windows.ContentControl
    {
        public bool? IsChecked { get; set; }
    }

    public class ComboBox : System.Windows.Control
    {
        public int SelectedIndex { get; set; }
        public object? SelectedValue { get; set; }
    }

    public class TextBox : System.Windows.Control
    {
        public string Text { get; set; } = string.Empty;
    }

    public class UserControl : System.Windows.Control
    {
        public void InitializeComponent() { }
    }
}

namespace System.Windows.Shapes
{
    public class Shape : System.Windows.FrameworkElement
    {
        public System.Windows.Media.Brush? Fill { get; set; }
        public System.Windows.Media.Brush? Stroke { get; set; }
        public double StrokeThickness { get; set; }
    }

    public class Ellipse : Shape { }

    public class Path : Shape { }
}

namespace System.Windows.Media
{
    public struct Color
    {
        public byte A, R, G, B;
        public static Color FromRgb(byte r, byte g, byte b) => new() { A = 255, R = r, G = g, B = b };
        public static Color FromArgb(byte a, byte r, byte g, byte b) => new() { A = a, R = r, G = g, B = b };
    }

    public class Brush { public double Opacity { get; set; } }

    public class SolidColorBrush : Brush
    {
        public SolidColorBrush() { }
        public SolidColorBrush(Color color) { Color = color; }
        public Color Color { get; set; }
    }

    public static class Brushes
    {
        public static Brush Gray { get; } = new SolidColorBrush();
        public static Brush Transparent { get; } = new SolidColorBrush();
    }

    public static class ColorConverter
    {
        public static object ConvertFromString(string value) => new Color();
    }
}

namespace System.Windows.Data
{
    public interface IValueConverter
    {
        object Convert(object value, Type targetType, object parameter, CultureInfo culture);
        object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture);
    }

    public class Binding
    {
        public static readonly object DoNothing = new();
    }
}

namespace System.Windows.Input
{
    public interface ICommand
    {
        event EventHandler? CanExecuteChanged;
        bool CanExecute(object? parameter);
        void Execute(object? parameter);
    }

    public enum MouseButtonState { Released, Pressed }

    public class MouseButtonEventArgs : System.Windows.RoutedEventArgs
    {
        public MouseButtonState ButtonState { get; set; }
        public int ClickCount { get; set; }
    }
}

namespace System.Windows.Threading
{
    public class Dispatcher
    {
        public void Invoke(Action action) => action();
        public T Invoke<T>(Func<T> action) => action();
    }

    public class DispatcherTimer
    {
        public TimeSpan Interval { get; set; }
        public event EventHandler? Tick;
        public void Start() { }
        public void Stop() { }
        public void RaiseForStubs() => Tick?.Invoke(this, EventArgs.Empty);
    }
}

namespace System.Windows.Interop
{
    public delegate IntPtr HwndSourceHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled);

    public class WindowInteropHelper
    {
        public WindowInteropHelper(System.Windows.Window window) { }
        public IntPtr Handle { get; } = IntPtr.Zero;
    }

    public class HwndSource
    {
        public static HwndSource? FromHwnd(IntPtr hwnd) => null;
        public void AddHook(HwndSourceHook hook) { }
    }
}

namespace Microsoft.Win32
{
    public class FileDialog
    {
        public string FileName { get; set; } = string.Empty;
        public string Filter { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public bool? ShowDialog() => false;
    }

    public class OpenFileDialog : FileDialog { }

    public class SaveFileDialog : FileDialog { }
}

namespace System.Windows.Forms
{
    public enum ToolTipIcon { None, Info, Warning, Error }

    public class ToolStripItem
    {
        public string Text { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public bool Visible { get; set; } = true;
        public event EventHandler? Click;
        public void RaiseForStubs() => Click?.Invoke(this, EventArgs.Empty);
    }

    public class ToolStripMenuItem : ToolStripItem
    {
        public ToolStripMenuItem() { }
        public ToolStripMenuItem(string text) { Text = text; }
    }

    public class ToolStripSeparator : ToolStripItem { }

    public class ToolStripItemCollection : List<ToolStripItem> { }

    public class ContextMenuStrip
    {
        public ToolStripItemCollection Items { get; } = new();
        public event EventHandler<CancelEventArgs>? Opening;
        public void RaiseForStubs() => Opening?.Invoke(this, new CancelEventArgs());
    }

    public class NotifyIcon : IDisposable
    {
        public bool Visible { get; set; }
        public string Text { get; set; } = string.Empty;
        public ContextMenuStrip? ContextMenuStrip { get; set; }
        public System.Drawing.Icon? Icon { get; set; }
        public event EventHandler? DoubleClick;
        public event EventHandler? BalloonTipClicked;
        public void ShowBalloonTip(int timeout, string title, string text, ToolTipIcon icon) { }
        public void Dispose() { }
        public void RaiseForStubs() { DoubleClick?.Invoke(this, EventArgs.Empty); BalloonTipClicked?.Invoke(this, EventArgs.Empty); }
    }
}

namespace System.Drawing
{
    public struct Color
    {
        public static Color Transparent => new();
        public static Color FromArgb(int r, int g, int b) => new();
        public static Color FromArgb(int a, int r, int g, int b) => new();
    }

    public class Brush : IDisposable { public void Dispose() { } }

    public class SolidBrush : Brush
    {
        public SolidBrush(Color color) { }
    }

    public class Pen : IDisposable
    {
        public Pen(Color color, float width) { }
        public void Dispose() { }
    }

    public class Image : IDisposable { public void Dispose() { } }

    public class Bitmap : Image
    {
        public Bitmap(int width, int height) { }
        public IntPtr GetHicon() => IntPtr.Zero;
    }

    public class Icon : IDisposable, ICloneable
    {
        public static Icon FromHandle(IntPtr handle) => new();
        public object Clone() => new Icon();
        public void Dispose() { }
    }

    public class Graphics : IDisposable
    {
        public static Graphics FromImage(Image image) => new();
        public Drawing2D.SmoothingMode SmoothingMode { get; set; }
        public void Clear(Color color) { }
        public void FillEllipse(Brush brush, int x, int y, int width, int height) { }
        public void DrawEllipse(Pen pen, int x, int y, int width, int height) { }
        public void FillRectangle(Brush brush, int x, int y, int width, int height) { }
        public void Dispose() { }
    }
}

namespace System.Drawing.Drawing2D
{
    public enum SmoothingMode { Default, HighSpeed, HighQuality, None, AntiAlias }
}
