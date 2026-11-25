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
    private TaskCompletionSource<IReadOnlyList<RuntimeRelease>>? _releaseLookup;
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
                UiSmokeChecks.RequireText(window, "Looking up PHP NTS releases…");
                UiSmokeChecks.Validate(window);
                preview._releaseLookup.SetResult([]);
                await lookup;
                window.UpdateLayout();
                UiSmokeChecks.RequireText(window, "No compatible releases found");

                preview._releaseLookup = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var cancelledLookup = window.PrepareSmokeSectionAsync("Updates", checkReleases: true);
                window.UpdateLayout();
                var cancel = FindButton(window, "Cancel lookup")
                    ?? throw new InvalidOperationException("The in-page lookup must expose a cancel action.");
                cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await cancelledLookup.WaitAsync(TimeSpan.FromSeconds(5));
                window.UpdateLayout();
                if (FindButton(window, "Cancel lookup") is not null)
                    throw new InvalidOperationException("Cancelled release lookup is still shown as active.");
                preview._releaseLookup = null;
                Console.WriteLine("PASS desktop release lookup shows a dedicated loading state, handles empty results and can be cancelled");
                await preview.CheckExtensionDialogAsync(window, manager, captureDirectory);
                await preview.CheckRenameDialogAsync(window, captureDirectory);
                await preview.CheckSiteActivationAsync(window, captureDirectory);
                foreach (var preference in new[] { ThemePreference.Light, ThemePreference.Dark })
                foreach (var width in new[] { 560, 1000 })
                foreach (var section in new[] { "Sites", "Caddy", "PHP Versions", "Mail Trap", "Logs", "Updates", "Settings" })
                {
                    theme.SetPreference(preference);
                    window.Width = width;
                    window.Height = 640;
                    await window.PrepareSmokeSectionAsync(section);
                    await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                    var checks = UiSmokeChecks.Validate(window);
                    var path = Path.Combine(captureDirectory, $"{section.Replace(' ', '-')}-{preference}-{width}.png");
                    Capture(window, path);
                    File.WriteAllLines(path + ".qa.txt", checks);
                    Console.WriteLine($"PASS desktop {section}, {preference}, {width}");
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

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var name = targetMethod!.Name;
        if (name == "get_Snapshot") return _snapshot;
        if (name == "add_Changed") { _changed += (EventHandler)args![0]!; return null; }
        if (name == "remove_Changed") { _changed -= (EventHandler)args![0]!; return null; }
        if (name == "GetMessagesAsync") return Task.FromResult<IReadOnlyList<MailMessageSummary>>([
            new("welcome", "Welcome to the local application", "hello@example.test", "developer@example.test", new DateTimeOffset(2026, 11, 25, 18, 0, 0, TimeSpan.Zero)),
            new("reset", "Password reset requested", "support@example.test", "developer@example.test", new DateTimeOffset(2026, 11, 25, 17, 58, 0, TimeSpan.Zero))]);
        if (name == "GetMessageTextAsync") return Task.FromResult(args![0]?.ToString() == "reset" ? "Development reset message. No real email was sent." : "Your local development account is ready.");
        if (name == "GetLogLinesAsync") return Task.FromResult<IReadOnlyList<string>>(["{\"level\":\"Information\",\"message\":\"Environment ready. All services are healthy.\"}"]);
        if (name == "GetAvailableReleasesAsync") return _releaseLookup?.Task.WaitAsync((CancellationToken)args![1]!) ?? Task.FromResult<IReadOnlyList<RuntimeRelease>>([]);
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
            SetServiceStatus((string)args![0]!, ServiceStatus.Running);
            return Task.CompletedTask;
        }
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
