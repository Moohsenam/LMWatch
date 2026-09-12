// Stand-ins for the fields and methods the XAML compiler generates on Windows.
// Only used by the offline type-check; never part of the shipped app.
#nullable enable
using System.Windows.Controls;
using System.Windows.Shapes;

namespace ClaudeWatch.App
{
    public partial class MainWindow
    {
        internal Border RootShell = new();
    }

    public partial class SetupWizard
    {
        internal StackPanel StepLanguage = new();
        internal StackPanel StepApps = new();
        internal StackPanel StepZone = new();
        internal StackPanel StepKey = new();
        internal CheckBox WantClaude = new();
        internal CheckBox WantChatGpt = new();
        internal TextBlock AppsWarning = new();
        internal TextBlock StepDots = new();
        internal Button BackButton = new();
        internal Button NextButton = new();
        internal Button SkipButton = new();
    }

    public partial class GraceOverlay
    {
        internal Shape Ring = new Ellipse();
        internal Shape Tick = new Path();
        internal TextBlock Countdown = new();
        internal TextBlock Caption = new();
        internal TextBlock Headline = new();
        internal TextBlock Body = new();
        internal Border BarTrack = new();
        internal Border BarFill = new();
        internal Button StopNow = new();
        internal Button OpenApp = new();
    }
}

namespace ClaudeWatch.App.Views
{
    public partial class DashboardView { }
    public partial class BuyView { }
    public partial class LicenceView { }
    public partial class OrdersView { }
    public partial class UsageView { }
    public partial class ProcessesView { }
    public partial class NetworkView { }
    public partial class ActivityView { }
    public partial class SettingsView { }
    public partial class AboutView { }
}
