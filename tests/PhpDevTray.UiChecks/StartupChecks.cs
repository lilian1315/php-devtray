using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PhpDevTray.App;
using PhpDevTray.Infrastructure;
using Windows.ApplicationModel;

internal static class StartupChecks
{
    public static void Run()
    {
        var command = StartupRegistration.BuildCommand(@"C:\Apps With Spaces\PHP DevTray.exe", @"C:\Profiles With Spaces\dev\");
        Require(command == "\"C:\\Apps With Spaces\\PHP DevTray.exe\" --startup --data-directory \"C:\\Profiles With Spaces\\dev\\\\\"",
            "Startup must quote the executable and preserve the selected profile, including trailing backslashes.");
        Require(!StartupRegistration.FromState(StartupTaskState.DisabledByUser).Enabled &&
                StartupRegistration.FromState(StartupTaskState.DisabledByUser).Notice is not null,
            "A task disabled in Windows must remain disabled with actionable guidance.");

        var root = Path.Combine(Path.GetTempPath(), "PhpDevTray-startup-checks-" + Guid.NewGuid().ToString("N"));
        var manager = Bootstrapper.Create(root);
        var registration = new FakeRegistration();
        var window = new ManagerWindow(manager, registration);
        try
        {
            window.PrepareSmokeSectionAsync("Settings").GetAwaiter().GetResult();
            var checkbox = Descendants<Wpf.Ui.Controls.ToggleSwitch>(window.PageContent).Single(c => System.Windows.Automation.AutomationProperties.GetName(c) == "Start PHP DevTray with Windows");
            Require(checkbox.IsChecked == false && checkbox.IsEnabled, "Startup must initially reflect Windows' disabled state.");
            Click(checkbox, true);
            Require(registration.Changes.SequenceEqual([true]) && checkbox.IsChecked == true, "Enabling startup must update Windows registration and read back the resulting state.");
            Click(checkbox, false);
            Require(registration.Changes.SequenceEqual([true, false]) && checkbox.IsChecked == false, "Disabling startup must remove automatic launch.");
            registration.Failure = new IOException("Startup registration denied");
            Click(checkbox, true);
            Require(checkbox.IsChecked == false && window.NotificationHistory.Any(message => message.Message == "Startup registration denied"),
                "A failed registration must restore the previous checkbox state and show the error.");
            registration.Failure = null;
            registration.State = new(false, "Enable PHP DevTray in Windows Startup settings.");
            window.PrepareSmokeSectionAsync("Sites").GetAwaiter().GetResult();
            window.PrepareSmokeSectionAsync("Settings").GetAwaiter().GetResult();
            Require(Descendants<TextBlock>(window.PageContent).Any(t => t.Text == registration.State.Notice), "Reopening settings must reread Windows state and show disabled-task guidance.");
            Require(!window.IsVisible, "Changing startup settings must not show a hidden manager.");
        }
        finally
        {
            window.BeginShutdown();
            window.CloseForExit();
            manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        Console.WriteLine("PASS: startup settings enable/disable Windows registration, restore failed changes, and refresh Windows overrides without showing the manager.");
    }

    private static void Click(Wpf.Ui.Controls.ToggleSwitch checkbox, bool enabled)
    {
        checkbox.IsChecked = enabled;
        checkbox.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is Window window)
        {
            window.Measure(new Size(1120, 800)); window.Arrange(new Rect(0, 0, 1120, 800)); window.UpdateLayout();
            var frame = new System.Windows.Threading.DispatcherFrame();
            _ = window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
        var pending = new Stack<DependencyObject>(); pending.Push(root);
        var seen = new HashSet<DependencyObject>();
        while (pending.TryPop(out var element))
        {
            if (!seen.Add(element)) continue;
            if (element is T item) yield return item;
            foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>()) pending.Push(child);
            if (element is Wpf.Ui.Controls.CardControl { Header: DependencyObject header }) pending.Push(header);
            if (element is System.Windows.Media.Visual)
                for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(element); index++)
                    pending.Push(System.Windows.Media.VisualTreeHelper.GetChild(element, index));
        }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class FakeRegistration : IStartupRegistration
    {
        public StartupRegistrationState State { get; set; } = new(false);
        public Exception? Failure { get; set; }
        public List<bool> Changes { get; } = [];
        public Task<StartupRegistrationState> ReadAsync() => Task.FromResult(State);
        public Task<StartupRegistrationState> SetEnabledAsync(bool enabled)
        {
            if (Failure is not null) return Task.FromException<StartupRegistrationState>(Failure);
            Changes.Add(enabled);
            return Task.FromResult(State = new(enabled));
        }
    }
}
