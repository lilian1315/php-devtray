using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PhpDevTray.App;
using PhpDevTray.Core;

// Isolated visual acceptance host: no service processes, registry writes or workspace changes.
public class DesktopPreview : DispatchProxy
{
    private EventHandler? _changed;
    private readonly List<string> _restartCalls = [];
    private readonly List<string> _startCalls = [];
    private int _stopAllCalls;
    private TaskCompletionSource<IReadOnlyList<RuntimeRelease>>? _releaseLookup;
    private ServiceKind? _lastReleaseKind;
    private TaskCompletionSource? _siteSave;
    private IReadOnlyList<PhpExtensionInfo> _extensions =
        [new() { Name = "openssl", IsAvailable = true }, new() { Name = "mbstring", IsAvailable = true }];
    private bool _rejectExtensionBatch;
    private int _extensionBatchCalls;
    private IReadOnlyList<PhpExtensionChange> _lastExtensionBatch = [];
    private EnvironmentSnapshot _snapshot = new()
    {
        DataDirectory = Path.GetTempPath(), LastSection = "Sites",
        Sites = [new("app.localhost", @"C:\Projects\app\public", "php:8.5")],
        SiteRoots = new Dictionary<string, string?> { ["app.localhost"] = @"C:\Projects\app\public" },
        PhpInstallations = [new() { Id = "php:8.5", Channel = "8.5", Version = "8.5.13", Directory = @"C:\PHP\8.5", Port = 9085 }],
        DefaultPhpChannel = "8.5",
        Services = [new() { Id = "caddy", DisplayName = "Caddy", Kind = ServiceKind.Caddy, Status = ServiceStatus.Running },
            new() { Id = "php:8.5", DisplayName = "PHP 8.5.13", Kind = ServiceKind.Php, Status = ServiceStatus.Running, Endpoint = "127.0.0.1:9085" },
            new() { Id = "mailpit", DisplayName = "Mail Trap", Kind = ServiceKind.Mailpit, Status = ServiceStatus.Running }]
    };

    internal static int Run(App app, string? captureDirectory = null)
    {
        var manager = Create<IEnvironmentManager, DesktopPreview>();
        using var theme = new ThemeManager();
        manager.Changed += (_, _) => theme.SetPreference(manager.Snapshot.Theme);
        var window = new ManagerWindow(manager, new PreviewStartup()) { Title = "PHP DevTray — isolated UI acceptance" };
        manager.Changed += (_, _) => window.Render(manager.Snapshot);
        window.Closed += (_, _) => app.Shutdown();
        if (captureDirectory is not null) window.Loaded += async (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(captureDirectory);
                var preview = (DesktopPreview)manager;
                preview._releaseLookup = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var lookup = window.PrepareSmokeSectionAsync("Updates", checkReleases: true);
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                var stablePicker = Find<ComboBox>(window, control => control.ItemsSource is IReadOnlyList<RuntimeRelease>);
                UiSmokeChecks.Validate(window);
                preview._releaseLookup.SetResult([]);
                await lookup;
                window.UpdateLayout();
                Require(ReferenceEquals(stablePicker, Find<ComboBox>(window, control => control.ItemsSource is IReadOnlyList<RuntimeRelease>)), "Release lookup must preserve its input controls.");

                preview._releaseLookup = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var cancelledLookup = window.PrepareSmokeSectionAsync("Updates", checkReleases: true);
                window.UpdateLayout();
                var active = window.TrackedOperations.Snapshot.Single(item => item.State == OperationState.Running && item.CanCancel);
                window.TrackedOperations.Cancel(active.Id);
                await cancelledLookup.WaitAsync(TimeSpan.FromSeconds(5));
                Require(window.TrackedOperations.Snapshot.All(item => item.State != OperationState.Running), "Cancelled release lookup must leave no active operation.");
                preview._releaseLookup = null;
                Console.WriteLine("PASS desktop release lookup preserves inputs, handles empty results and supports central cancellation");
                var tabs = Find<TabControl>(window, control => AutomationProperties.GetName(control) == "Runtime to install")!;
                var tabsPeer = new TabControlAutomationPeer(tabs);
                foreach (var tab in tabs.Items.OfType<TabItem>().Reverse().ToArray())
                {
                    var peer = tabsPeer.GetChildren().Single(item => item.GetName() == (string)tab.Header);
                    var selection = peer.GetPattern(PatternInterface.SelectionItem) as ISelectionItemProvider
                        ?? throw new InvalidOperationException("Runtime tabs must expose native selection.");
                    selection.Select();
                    await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                    Require(preview._lastReleaseKind == (ServiceKind)tab.Tag && tab.IsSelected,
                        "Selecting a runtime tab must fetch releases for that runtime and expose native selection.");
                    Require(ReferenceEquals(tabs, Find<TabControl>(window, control => AutomationProperties.GetName(control) == "Runtime to install")),
                        "Switching runtime tabs must preserve the tab strip and keyboard focus.");
                }
                Console.WriteLine("PASS runtime tabs select PHP NTS, FrankenPHP, Caddy and Mailpit through native automation without replacing controls");
                await preview.CheckExtensionDialogAsync(window, manager, captureDirectory);
                await preview.CheckRenameDialogAsync(window, captureDirectory);
                await preview.CheckSiteActivationAsync(window, captureDirectory);
                await CheckFluentShellAsync(window);
                await preview.CheckRestartActionsAsync(window);
                await CheckActivityAsync(window);
                foreach (var preference in new[] { ThemePreference.Light, ThemePreference.Dark })
                foreach (var width in new[] { 560, 1000 })
                foreach (var section in new[] { "Overview", "Sites", "Caddy", "PHP", "Mail Trap", "Logs", "Updates", "Settings" })
                {
                    theme.SetPreference(preference);
                    window.MinWidth = width;
                    window.Width = width;
                    window.Height = 640;
                    await window.PrepareSmokeSectionAsync(section);
                    await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                    var checks = UiSmokeChecks.Validate(window);
                    var path = Path.Combine(captureDirectory, $"{section.Replace(' ', '-')}-{preference}-{width}.png");
                    Capture(window, path);
                    File.WriteAllLines(path + ".qa.txt", checks);
                    Console.WriteLine($"PASS desktop {section}, {preference}, requested {width}, actual {window.ActualWidth:0}");
                }
                window.CloseForExit();
            }
            catch (Exception error) { Console.Error.WriteLine(error); app.Shutdown(1); }
        };
        window.Show();
        return app.Run();
    }

    private async Task CheckExtensionDialogAsync(Window owner, IEnvironmentManager manager, string directory)
    {
        var dialog = new ExtensionsDialog(manager, "8.5", CancellationToken.None) { Owner = owner, Width = 580 };
        await dialog.LoadForSmokeAsync();
        dialog.Show();
        try
        {
            await dialog.Dispatcher.InvokeAsync(dialog.UpdateLayout, DispatcherPriority.ApplicationIdle);
            var apply = FindButton(dialog, "Apply changes")!;
            Require(!apply.IsEnabled, "Apply must be disabled until a change is staged.");
            var search = Find<TextBox>(dialog, control => AutomationProperties.GetName(control) == "Search extensions")!;
            search.Text = "ssl";
            await dialog.Dispatcher.InvokeAsync(dialog.UpdateLayout, DispatcherPriority.ApplicationIdle);
            await ToggleAsync("openssl", true);
            search.Text = "mb";
            await dialog.Dispatcher.InvokeAsync(dialog.UpdateLayout, DispatcherPriority.ApplicationIdle);
            await ToggleAsync("mbstring", true);
            UiSmokeChecks.RequireText(dialog, "2 pending changes");
            Require(apply.IsEnabled && _extensionBatchCalls == 0, "Changing two checks must remain local before Apply.");
            search.Clear();
            dialog.UpdateLayout();
            Capture(dialog, Path.Combine(directory, "Extensions-pending.png"));
            search.Text = "mb";
            _rejectExtensionBatch = true;
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            dialog.UpdateLayout();
            UiSmokeChecks.RequireText(dialog, "Fixture validation failed.");
            UiSmokeChecks.RequireText(dialog, "2 pending changes");
            Require(search.Text == "mb" && apply.IsEnabled && ExtensionCheckBox("mbstring").IsChecked == true,
                "Failed Apply must preserve the search and checked modules for retry.");
            _rejectExtensionBatch = false;
            search.Clear();
            dialog.UpdateLayout();
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            dialog.UpdateLayout();
            UiSmokeChecks.RequireText(dialog, "No pending changes");
            Require(!apply.IsEnabled && _extensionBatchCalls == 2 && _lastExtensionBatch.ToHashSet().SetEquals([new("openssl", true), new("mbstring", true)]),
                "Each Apply click must send exactly one call containing both requested changes.");
            await ToggleAsync("openssl", false);
            FindButton(dialog, "Discard")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            dialog.UpdateLayout();
            Require(ExtensionCheckBox("openssl").IsChecked == true && _extensionBatchCalls == 2,
                "Discard must restore the applied check state without calling the manager.");
            Console.WriteLine("PASS desktop extension selection, atomic Apply, failure retry, preserved search and Discard");
        }
        finally
        {
            var discard = FindButton(dialog, "Discard");
            if (discard?.IsEnabled == true) discard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            dialog.Close();
        }

        CheckBox ExtensionCheckBox(string name) => Find<CheckBox>(dialog, control =>
            AutomationProperties.GetName(control).EndsWith(" " + name, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"No checkbox for {name}.");
        async Task ToggleAsync(string name, bool expected)
        {
            var box = ExtensionCheckBox(name);
            ((IToggleProvider)new CheckBoxAutomationPeer(box).GetPattern(PatternInterface.Toggle)!).Toggle();
            await dialog.Dispatcher.InvokeAsync(dialog.UpdateLayout, DispatcherPriority.ApplicationIdle);
            Require(box.IsChecked == expected, $"The native toggle for {name} did not change to {expected}.");
        }
    }

    private async Task CheckRenameDialogAsync(ManagerWindow owner, string directory)
    {
        await owner.PrepareSmokeSectionAsync("Sites");
        var original = _snapshot.Sites.Single();
        var rename = typeof(ManagerWindow).GetMethod("RenameSiteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var interaction = owner.Dispatcher.InvokeAsync(() =>
        {
            var dialog = owner.OwnedWindows.OfType<Window>().Single(window => window.Title == "Rename hostname");
            try
            {
                dialog.UpdateLayout();
                var input = Find<TextBox>(dialog, control => AutomationProperties.GetName(control) == "New hostname")!;
                var save = FindButton(dialog, "Rename site")!;
                Require(input.Text == original.Hostname && input.SelectedText == original.Hostname, "Rename must prefill and select the hostname.");
                Capture(dialog, Path.Combine(directory, "Rename-hostname.png"));
                input.Text = "invalid hostname";
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                dialog.UpdateLayout();
                UiSmokeChecks.RequireText(dialog, "Enter a valid local hostname.");
                Require(dialog.IsVisible && input.IsEnabled, "A rejected name must leave the dialog open and editable.");
                input.Text = "renamed.localhost";
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(!dialog.IsVisible, "A successful rename must dismiss the dialog.");
            }
            finally { if (dialog.IsVisible) dialog.Close(); }
        }, DispatcherPriority.ApplicationIdle);
        await (Task)rename.Invoke(owner, [original])!;
        await interaction.Task;
        owner.UpdateLayout();
        UiSmokeChecks.RequireText(owner, "renamed.localhost");
        _snapshot = _snapshot with { Sites = [original] };
        _changed?.Invoke(this, EventArgs.Empty);
        Console.WriteLine("PASS desktop hostname dialog prefills the host, explains validation errors, and updates Sites after Save");
    }

    private static void Capture(Window window, string path)
    {
        var visual = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private async Task CheckSiteActivationAsync(ManagerWindow window, string directory)
    {
        await window.PrepareSmokeSectionAsync("Sites");
        window.Width = 560;
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
        UiSmokeChecks.RequireText(window, "Serving");
        var disable = FindButton(window, "Disable app.localhost")!;
        Require(disable.IsVisible && disable.IsEnabled, "Disabling a serving site must be available directly on its card.");
        var original = _snapshot;
        _siteSave = new(TaskCreationOptions.RunContinuationsAsynchronously);
        disable.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
        UiSmokeChecks.RequireText(window, "Disabling…");
        Require(!disable.IsEnabled && _snapshot.Sites.Single().Enabled, "Pending activation must reflect the unfinished change without accepting another click.");
        _siteSave.SetException(new InvalidOperationException("Fixture activation failed."));
        _siteSave = null;
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
        UiSmokeChecks.RequireText(window, "Fixture activation failed.");
        UiSmokeChecks.RequireText(window, "Serving");
        disable = FindButton(window, "Disable app.localhost")!;
        Require(disable.IsEnabled, "Failed activation must leave the original action available for retry.");
        disable.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
        UiSmokeChecks.RequireText(window, "Disabled");
        UiSmokeChecks.Validate(window);
        Capture(window, Path.Combine(directory, "Sites-disabled.png"));
        FindButton(window, "Enable app.localhost")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
        UiSmokeChecks.RequireText(window, "Serving");
        SetServiceStatus("caddy", ServiceStatus.Stopped);
        window.UpdateLayout();
        UiSmokeChecks.RequireText(window, "Enabled · Offline");
        UiSmokeChecks.Validate(window);
        Capture(window, Path.Combine(directory, "Sites-offline.png"));
        var start = FindButton(window, "Start Caddy")!;
        Require(start.IsVisible && start.IsEnabled, "The Sites page must offer to start Caddy when an enabled site is offline.");
        start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
        UiSmokeChecks.RequireText(window, "Serving");
        _snapshot = original;
        _changed?.Invoke(this, EventArgs.Empty);
        Console.WriteLine("PASS desktop site activation, pending feedback, failure and retry, offline state, and Start Caddy");
    }

    private async Task SaveSiteAsync(WebSite site, Task? pending)
    {
        if (pending is not null) await pending;
        _snapshot = _snapshot with { Sites = _snapshot.Sites.Where(s => s.Hostname != site.Hostname).Append(site).ToArray() };
        _changed?.Invoke(this, EventArgs.Empty);
    }

    private void SetServiceStatus(string id, ServiceStatus status)
    {
        _snapshot = _snapshot with { Services = _snapshot.Services.Select(service => service.Id == id ? service with { Status = status } : service).ToArray() };
        _changed?.Invoke(this, EventArgs.Empty);
    }

    private static Button? FindButton(DependencyObject parent, string name)
        => Find<Button>(parent, button => AutomationProperties.GetName(button) == name);

    private static T? Find<T>(DependencyObject parent, Func<T, bool> match) where T : DependencyObject
    {
        if (parent is T candidate && match(candidate)) return candidate;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (Find(VisualTreeHelper.GetChild(parent, i), match) is { } child) return child;
        return null;
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private static async Task CheckFluentShellAsync(ManagerWindow window)
    {
        var overview = Find<Wpf.Ui.Controls.NavigationViewItem>(window, item => item.TargetPageTag == "Overview")
            ?? throw new InvalidOperationException("The Fluent pane must contain an Overview navigation item.");
        var peer = UIElementAutomationPeer.CreatePeerForElement(overview)
            ?? throw new InvalidOperationException("Navigation items must expose an automation peer.");
        ((ISelectionItemProvider)peer.GetPattern(PatternInterface.SelectionItem)!).Select();
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
        Require(overview.IsActive, "Invoking a native navigation item must update its selected state.");
        Require(((ISelectionItemProvider)peer.GetPattern(PatternInterface.SelectionItem)!).IsSelected,
            "Screen-reader selection must match the visibly active page.");
        UiSmokeChecks.RequireText(window, "Your local development environment at a glance.");
        await window.VerifyWindowLifecycleForSmokeAsync();
        Require(window.IsVisible && overview.IsActive, "Close/minimize/restore must preserve the Fluent shell and selected page.");
        Console.WriteLine("PASS Fluent navigation automation, selected page, minimize/close-to-tray and maximized restore");
    }
    private static async Task CheckActivityAsync(ManagerWindow window)
    {
        await window.PrepareSmokeSectionAsync("Logs");
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
        var viewer = Find<LogViewer>(window, _ => true)!;
        var search = Find<TextBox>(window, item => AutomationProperties.GetName(item) == "Search activity")!;
        Require(search.IsVisible && search.TranslatePoint(new Point(0, search.ActualHeight), window).Y <= viewer.TranslatePoint(new Point(), window).Y,
            "Search must remain visible on its own row above the logs.");
        var filter = Find<ComboBox>(window, item => AutomationProperties.GetName(item) == "Filter activity by service")!;
        filter.SelectedValue = "application"; window.UpdateLayout();
        UiSmokeChecks.RequireText(window, "PHP DevTray");
        Require(!viewer.Text.Contains("[application]") && viewer.Text.Contains("Environment ready") && !viewer.Text.Contains("upstream connection"),
            "PHP DevTray must filter application records while hiding their redundant source label.");
        filter.SelectedValue = "All services";
        var buffer = new ActivityBuffer(); var now = DateTimeOffset.Now;
        for (var index = 0; index < 200; index++) buffer.Append("php", $"entry {index:000}", now.AddSeconds(index));
        viewer.UpdateEntries(buffer.Snapshot.Reverse().ToArray());
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
        Require(viewer.Text.IndexOf("entry 000", StringComparison.Ordinal) < viewer.Text.IndexOf("entry 199", StringComparison.Ordinal)
            && viewer.VerticalOffset > 0 && viewer.VerticalOffset >= viewer.ExtentHeight - viewer.ViewportHeight - 2,
            "The newest entry must be last, and initial layout must show the bottom of the stream.");
        viewer.ScrollToHome(); window.UpdateLayout();
        buffer.Append("php", "entry 200", now.AddSeconds(200)); viewer.UpdateEntries(buffer.Snapshot);
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
        Require(viewer.VerticalOffset < 1, "Appending entries must respect a user who scrolled back.");
        search.Text = "entry 050"; viewer.FindMatch();
        Require(viewer.SelectedText == "entry 050" && search.IsVisible, "Search must select matching log text without hiding its input.");
        search.Clear();
        Console.WriteLine("PASS Activity ordering, initial tail scroll, preserved history scroll, PHP DevTray filter and persistent search row");
    }
    private async Task CheckRestartActionsAsync(ManagerWindow window)
    {
        var original = _snapshot;
        try
        {
            _snapshot = original with { Services = [.. original.Services, new() { Id = "php:8.4", Kind = ServiceKind.Php, DisplayName = "PHP 8.4", Status = ServiceStatus.Stopped }] };
            _changed?.Invoke(this, EventArgs.Empty);
            await window.PrepareSmokeSectionAsync("Overview");
            _restartCalls.Clear();
            FindButton(window, "Restart PHP")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(_restartCalls.SequenceEqual(["php:8.5"]), "Restart PHP must restart running PHP engines only.");
            FindButton(window, "Restart Web server")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            FindButton(window, "Restart Mail Trap")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(_restartCalls.SequenceEqual(["php:8.5", "caddy", "mailpit"]), "Overview restart buttons must target their own service.");
            await window.PrepareSmokeSectionAsync("PHP");
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
            FindButton(window, "Restart PHP 8.5.13")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(_restartCalls.Last() == "php:8.5" && FindButton(window, "Restart PHP 8.4") is { IsVisible: false },
                "Runtime restart actions must target that row and appear only while running.");
            _startCalls.Clear(); _stopAllCalls = 0;
            FindButton(window, "Restart all running services")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(_stopAllCalls == 1 && _startCalls.Order().SequenceEqual(new[] { "caddy", "mailpit", "php:8.5" }.Order()) && _startCalls.Last() == "caddy",
                "Restart all must restore exactly the previously running services, with Caddy after its backends.");
            FindButton(window, "Stop all services")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(FindButton(window, "Restart all running services") is { IsVisible: false }, "Restart all must disappear when all services are stopped.");
            Require(FindButton(window, "Refresh environment") is null, "The environment refresh button must be removed.");
            Console.WriteLine("PASS Overview/runtime restart targets, running-only group restart, Caddy startup ordering and stopped-state actions");
        }
        finally { _snapshot = original; _changed?.Invoke(this, EventArgs.Empty); }
    }


    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var name = targetMethod!.Name;
        if (name == "get_Snapshot") return _snapshot;
        if (name is "get_Operations" or "get_Notifications" or "get_Activity") return null;
        if (name == "add_Changed") { _changed += (EventHandler)args![0]!; return null; }
        if (name == "remove_Changed") { _changed -= (EventHandler)args![0]!; return null; }
        if (name == "GetMessagesAsync") return Task.FromResult<IReadOnlyList<MailMessageSummary>>([
            new("welcome", "Welcome to the local application", "hello@example.test", "developer@example.test", new DateTimeOffset(2026, 11, 25, 18, 0, 0, TimeSpan.Zero)),
            new("reset", "Password reset requested", "support@example.test", "developer@example.test", new DateTimeOffset(2026, 11, 25, 17, 58, 0, TimeSpan.Zero))]);
        if (name == "GetMessageTextAsync") return Task.FromResult(args![0]?.ToString() == "reset" ? "Development reset message. No real email was sent." : "Your local development account is ready.");
        if (name == "GetLogLinesAsync") return Task.FromResult<IReadOnlyList<string>>([
            "— logs/application/2026-11-25.jsonl —",
            "{\"timestamp\":\"2026-11-25T12:00:00Z\",\"level\":\"Information\",\"message\":\"Environment ready. All services are healthy.\"}",
            "{\"timestamp\":\"2026-11-25T12:00:01Z\",\"level\":\"Warning\",\"message\":\"PHP configuration changed. Restart to apply it.\"}",
            "— logs/caddy/caddy.stderr.log —",
            "{\"ts\":1795608002,\"level\":\"error\",\"logger\":\"http.log.error\",\"msg\":\"upstream connection refused\",\"address\":\"127.0.0.1:9085\"}",
            "2026-11-25 13:00:03 PHP runtime ready on 127.0.0.1:9085"
        ]);
        if (name == "GetAvailableReleasesAsync")
        {
            _lastReleaseKind = (ServiceKind)args![0]!;
            return _releaseLookup?.Task.WaitAsync((CancellationToken)args[1]!) ?? Task.FromResult<IReadOnlyList<RuntimeRelease>>([]);
        }
        if (name == "GetPhpExtensionsAsync") return Task.FromResult(_extensions);
        if (name == "ApplyPhpExtensionChangesAsync")
        {
            _extensionBatchCalls++;
            _lastExtensionBatch = (IReadOnlyList<PhpExtensionChange>)args![1]!;
            if (_rejectExtensionBatch) return Task.FromException(new InvalidOperationException("Fixture validation failed."));
            var changes = _lastExtensionBatch.ToDictionary(change => change.Name, change => change.Enabled);
            _extensions = _extensions.Select(extension => changes.TryGetValue(extension.Name, out var enabled)
                ? extension with { IsEnabled = enabled, IsManaged = enabled } : extension).ToArray();
            return Task.CompletedTask;
        }
        if (name == "RenameSiteAsync")
        {
            var hostname = (string)args![1]!;
            if (hostname.Contains(' ')) return Task.FromException(new ArgumentException("Enter a valid local hostname."));
            _snapshot = _snapshot with { Sites = _snapshot.Sites.Select(site => site.Hostname == (string)args[0]!
                ? site with { Hostname = hostname } : site).ToArray() };
        }
        if (name == "DisposeAsync") return ValueTask.CompletedTask;
        if (name == "SaveSiteAsync") return SaveSiteAsync((WebSite)args![0]!, _siteSave?.Task);
        if (name == "StartServiceAsync")
        {
            _startCalls.Add((string)args![0]!);
            SetServiceStatus((string)args![0]!, ServiceStatus.Running);
            return Task.CompletedTask;
        }
        if (name == "RestartServiceAsync") { _restartCalls.Add((string)args![0]!); return Task.CompletedTask; }
        if (name == "StopAllAsync") _stopAllCalls++;
        if (name == "SetThemeAsync") _snapshot = _snapshot with { Theme = (ThemePreference)args![0]! };
        if (name == "SetLastSectionAsync") _snapshot = _snapshot with { LastSection = (string)args![0]! };
        if (name is "StopAllAsync" or "StartAllAsync")
            _snapshot = _snapshot with { Services = _snapshot.Services.Select(s => s with { Status = name == "StartAllAsync" ? ServiceStatus.Running : ServiceStatus.Stopped }).ToArray() };
        _changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    private sealed class PreviewStartup : IStartupRegistration
    {
        private bool _enabled;
        public Task<StartupRegistrationState> ReadAsync() => Task.FromResult(new StartupRegistrationState(_enabled));
        public Task<StartupRegistrationState> SetEnabledAsync(bool enabled) { _enabled = enabled; return ReadAsync(); }
    }
}
