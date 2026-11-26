using Button = Wpf.Ui.Controls.Button;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PhpDevTray.Core;
using Microsoft.Win32;

namespace PhpDevTray.App;

internal sealed partial class ManagerWindow : Wpf.Ui.Controls.FluentWindow
{
    private static readonly string[] Sections = ["Overview", "Sites", "Caddy", "PHP", "Mail Trap", "Logs", "Updates", "Settings"];
    private readonly IEnvironmentManager _manager;
    private readonly TextBlock _overall, _operationText, _pageTitle, _pageDescription;
    private readonly Button _globalAction;
    private readonly Button _globalRestart;
    private readonly Grid _contentHost;
    private readonly WrapPanel _pageActions;
    private bool _updatesPaused;
    private readonly Dictionary<string, Wpf.Ui.Controls.NavigationViewItem> _navigation = new();
    private readonly Dictionary<ServiceKind, TextBlock> _serviceLabels = new();
    private readonly Dictionary<ServiceKind, Button> _serviceToggles = new();
    private readonly Dictionary<string, PhpRow> _phpRows = new();
    private readonly Dictionary<ServiceKind, InlineService> _inlineServices = new();
    private readonly List<(Button Button, Func<bool> Enabled)> _bodyButtons = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly PageAutoRefresher _pageRefresher;
    private string? _autoRefreshPage, _autoRefreshError;
    private EnvironmentSnapshot _snapshot;
    private string _section, _bodySignature = string.Empty;
    private string? _renderedSection, _dismissedError, _operation;
    private OperationHandle? _foregroundOperation;
    private int _focusHold;
    private long _ignoreDeactivationUntil;
    private bool _closeForExit, _shutdown, _updatingInputs;
    private IReadOnlyList<MailMessageSummary> _messages = [];
    private IReadOnlyList<MailMessageSummary>? _renderedMessages;
    private string _mailText = string.Empty;
    private IReadOnlyList<string> _logLines = [];
    private string? _selectedMessageId, _loadedMessageId;
    private ListBox? _mailList;
    private TextBox? _mailSearch, _mailPreview;
    private LogViewer? _logOutput;
    private TextBlock? _mailSubject, _mailMetadata;
    private TextBlock? _mailResults;
    private Border? _mailNoResults;
    private Grid? _mailDetail;
    private CancellationTokenSource? _messageCancellation;
    private TabControl? _runtimeSelector;
    private ServiceKind _releaseKind = ServiceKind.Php;
    private string? _releasePhpChannel;
    private IReadOnlyList<RuntimeRelease> _releases = [];
    private string? _selectedReleaseId, _releaseError;
    private string? _visualCppRetryId;
    private bool _releaseCheckComplete;
    private CancellationTokenSource? _releaseCheck;
    private CancellationTokenSource? _installCancellation;
    private RuntimeInstallProgress? _installProgress;
    private ContentControl? _releaseContent;
    private ComboBox? _releaseVersionPicker;
    private ComboBox? _appearancePicker;
    private Wpf.Ui.Controls.ToggleSwitch? _pathSetting;
    private readonly IStartupRegistration _startupRegistration;
    private Wpf.Ui.Controls.ToggleSwitch? _startupSetting;
    private TextBlock? _startupNotice;
    private StartupRegistrationState? _startupState;
    private bool _startupChanging;
    private ComboBox? _defaultPhpPicker;
    private WindowState _restoreState = WindowState.Normal;

    public void RestoreWindow()
    {
        if (_shutdown) return;
        Render(_manager.Snapshot);
        if (WindowState == WindowState.Minimized) WindowState = _restoreState;
        Show(); Activate();
    }

    internal async Task VerifyWindowLifecycleForSmokeAsync()
    {
        if (!ShowInTaskbar || Topmost || AllowsTransparency || ResizeMode != ResizeMode.CanResize || !ExtendsContentIntoTitleBar)
            throw new InvalidOperationException("Expected a normal resizable desktop window.");
        WindowState = WindowState.Maximized;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        WindowState = WindowState.Minimized;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (IsVisible) throw new InvalidOperationException("Minimize must hide the window to the tray.");
        RestoreWindow();
        if (!IsVisible || WindowState != WindowState.Maximized) throw new InvalidOperationException("Tray restore must preserve maximized state.");
        Close();
        if (IsVisible) throw new InvalidOperationException("Close must hide the window without destroying it.");
        RestoreWindow();
        if (!IsVisible) throw new InvalidOperationException("A closed-to-tray window must be reusable.");
        WindowState = WindowState.Normal;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    internal void SetSmokeSection(string section)
    {
        if (!Sections.Contains(section)) throw new ArgumentException("Unknown manager section.", nameof(section));
        _suppressNavigationEffects = true;
        try { if (!_shellNavigation.Navigate(section)) Render(_snapshot); } finally { _suppressNavigationEffects = false; }
    }

    public void Render(EnvironmentSnapshot snapshot)
    {
        _snapshot = snapshot;
        if (snapshot.LastError is { Length: > 0 } remote && remote != _dismissedError) { _dismissedError = remote; ShowError(remote); }
        var runningCount = snapshot.Services.Count(s => s.Status == ServiceStatus.Running);
        _overall.Text = snapshot.IsBusy ? "Applying changes…" : snapshot.Services.Any(s => s.Status == ServiceStatus.Failed) ? "One or more services need attention" : AnyRunning ? $"{runningCount} {(runningCount == 1 ? "service" : "services")} running" : "All services stopped";
        _globalAction.Content = IconLabel(AnyRunning ? AppIcon.Stop : AppIcon.Play, AnyRunning ? "Stop all" : "Start all");
        AutomationProperties.SetName(_globalAction, AnyRunning ? "Stop all services" : "Start all services");
        _globalAction.IsEnabled = !Busy && snapshot.Services.Any(s => s.Status != ServiceStatus.NotInstalled);
        _globalRestart.Visibility = AnyRunning ? Visibility.Visible : Visibility.Collapsed;
        _globalRestart.IsEnabled = !Busy && AnyRunning;
        UpdateOverview();
        var signature = StructureSignature();
        if (_renderedSection != _section || _bodySignature != signature)
        {
            _renderedSection = _section; _bodySignature = signature; _bodyButtons.Clear(); _contentHost.Children.Clear(); _pageActions.Children.Clear(); _phpRows.Clear(); _inlineServices.Clear();
            _siteRows.Clear(); _siteServerControl = null; _restartButtons.Clear();
            _mailList = null; _mailSearch = null; _mailPreview = null; _mailResults = null; _mailNoResults = null; _mailDetail = null; _logOutput = null; _releaseContent = null; _releaseVersionPicker = null; _runtimeSelector = null; _pathSetting = null; _startupSetting = null; _startupNotice = null; _defaultPhpPicker = null; _appearancePicker = null;
            switch (_section)
            {
                case "Overview": BuildOverview(); break;
                case "Sites": BuildSites(); break;
                case "Caddy": BuildCaddy(); break;
                case "PHP": BuildPhp(); break;
                case "Mail Trap": BuildMail(); break;
                case "Logs": BuildLogs(); break;
                case "Updates": BuildInstall(); break;
                case "Settings": BuildSettings(); break;
            }
        }
        foreach (var row in _phpRows.Values) UpdatePhpRow(row);
        UpdateCaddy();
        if (_section == "Sites") UpdateSiteStates();
        foreach (var (kind, row) in _inlineServices)
        {
            var service = _snapshot.Services.FirstOrDefault(s => s.Kind == kind && s.Status != ServiceStatus.NotInstalled);
            row.Status.Text = $"{row.Title}  ·  {StatusLabel(service?.Status ?? ServiceStatus.NotInstalled)}";
        }
        if (_mailList is not null && !ReferenceEquals(_renderedMessages, _messages)) FillMailList();
        if (_section == "Updates") UpdateInstall();
        if (_section == "Settings") UpdateSettings();
        UpdateLogOutput();
        foreach (var registration in _bodyButtons.ToArray()) registration.Button.IsEnabled = registration.Enabled();
        foreach (var (button, running) in _restartButtons) button.Visibility = running() ? Visibility.Visible : Visibility.Collapsed;
        UpdatePageRefresh();
    }

    private string StructureSignature() => _section switch
    {
        "Sites" => string.Join("|", _snapshot.Sites.Select(site => site.Hostname)) + string.Join("|", _snapshot.Services.Select(service => service.Id + (service.Status != ServiceStatus.NotInstalled))),
        "Caddy" => HasRuntime(ServiceKind.Caddy).ToString(),
        "PHP" => string.Join("|", _snapshot.Services.Where(service => service.Kind is ServiceKind.Php or ServiceKind.FrankenPhp).Select(service => service.Id)),
        "Mail Trap" => $"{HasRuntime(ServiceKind.Mailpit)}:{MailIsRunning}",
        "Settings" => string.Join("|", _snapshot.PhpInstallations.Select(item => item.Channel)),
        _ => string.Empty
    };

    internal OperationTracker TrackedOperations => _operationTracker;
    internal IReadOnlyList<NotificationMessage> NotificationHistory => _notifications.History;

    private async Task OpenInstallAsync(ServiceKind kind)
    {
        if (_releaseKind != kind || _releasePhpChannel is not null) ResetReleaseSelection(kind);
        await NavigateAsync("Updates");
    }

    private async Task OpenPhpUpdatesAsync(string channel)
    {
        ResetReleaseSelection(ServiceKind.Php, channel);
        await NavigateAsync("Updates");
    }

    private void ResetReleaseSelection(ServiceKind kind, string? channel = null)
    {
        CancelReleaseCheck();
        _releaseKind = kind;
        _releasePhpChannel = channel;
        _releases = [];
        _selectedReleaseId = _releaseError = _visualCppRetryId = null;
        _releaseCheckComplete = false;
        _installProgress = null;
        _renderedSection = null;
    }

    private void CancelReleaseCheck()
    {
        var lookup = _releaseCheck;
        _releaseCheck = null;
        lookup?.Cancel();
    }

    private async Task BrowseRuntimeAsync(ServiceKind kind)
    {
        using var hold = HoldFocus();
        var executable = kind switch { ServiceKind.Php => "php.exe", ServiceKind.Caddy => "caddy.exe", ServiceKind.FrankenPhp => "frankenphp.exe", _ => "mailpit.exe" };
        var picker = new OpenFileDialog { Title = $"Select {executable} from an extracted distribution", Filter = $"{executable}|{executable}|Executables|*.exe", CheckFileExists = true, Multiselect = false };
        if (picker.ShowDialog(this) != true) return;
        await RunAsync($"Validating {kind} runtime…", () => kind switch
        {
            ServiceKind.Caddy or ServiceKind.FrankenPhp => _manager.RegisterWebRuntimeAsync(kind, picker.FileName),
            ServiceKind.Php => _manager.RegisterPhpAsync(picker.FileName),
            _ => _manager.RegisterMailpitAsync(picker.FileName),
        });
    }


    private Task ShowExtensionsAsync(string channel)
    {
        using var hold = HoldFocus();
        var dialog = new ExtensionsDialog(_manager, channel, _lifetime.Token) { Owner = this };
        dialog.ShowDialog();
        return Task.CompletedTask;
    }

    private async Task CheckReleasesAsync()
    {
        if (_releaseCheck is not null || _section != "Updates" || _shutdown) return;
        using var lookup = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        using var operation = _operationTracker.Begin($"Fetching {ReleaseTypeName()} releases", lookup.Cancel, CheckReleasesAsync, quiet: true);
        _releaseCheck = lookup;
        var kind = _releaseKind;
        var channel = _releasePhpChannel;
        _visualCppRetryId = null;
        _releaseError = null;
        Render(_snapshot);
        try
        {
            var releases = await _manager.GetAvailableReleasesAsync(kind, lookup.Token);
            if (_releaseCheck != lookup) return;
            _releases = kind == ServiceKind.Php && channel is not null
                ? releases.Where(release => ReleaseChannel(release) == channel).OrderByDescending(release => ParsePhpVersion(release.Version)).ToArray()
                : releases;
            _releaseCheckComplete = true;
            if (!_releases.Any(release => release.Id == _selectedReleaseId)) _selectedReleaseId = _releases.FirstOrDefault()?.Id;
            operation.Complete();
        }
        catch (OperationCanceledException error) when (lookup.IsCancellationRequested) { operation.Fail(error); }
        catch (OperationCanceledException) when (_releaseCheck == lookup)
        {
            operation.Fail(new OperationCanceledException());
        }
        catch (Exception exception)
        {
            if (_releaseCheck != lookup) return;
            _releaseCheckComplete = true;
            _releaseError = $"Could not read the publisher's release list. Check your network connection and retry, or import an extracted official runtime. {exception.Message}";
            operation.Fail(new InvalidOperationException(_releaseError, exception)); ShowError(_releaseError);
        }
        finally
        {
            if (_releaseCheck == lookup)
            {
                _releaseCheck = null;
                if (!_shutdown) Render(_snapshot);
            }
        }
    }

    private Task InstallSelectedReleaseAsync(RuntimeRelease release, bool installVisualCpp = false) => RunAsync($"Installing {release.DisplayName}…", async () =>
    {
        if (InstallationBlockReason(release) is { } reason) throw new InvalidOperationException(reason);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _installCancellation = cancellation;
        _operationTracker.ConfigureCurrent(cancellation.Cancel, () => InstallSelectedReleaseAsync(release, installVisualCpp));
        _visualCppRetryId = null;
        _releaseError = null;
        _installProgress = new RuntimeInstallProgress("Preparing download…");
        Render(_snapshot);
        try
        {
            var progress = new Progress<RuntimeInstallProgress>(update =>
            {
                if (!ReferenceEquals(_installCancellation, cancellation)) return;
                _installProgress = update;
                _foregroundOperation?.Report(update.Phase, update.TotalBytes is > 0 ? update.BytesReceived / (double)update.TotalBytes.Value : null);
            });
            if (installVisualCpp)
            {
                var restart = await PhpDevTray.Infrastructure.Runtimes.VisualCppInstaller.InstallAsync(progress, cancellation.Token);
                if (restart)
                {
                    _installProgress = new("Restart Windows to finish Visual C++ setup, then retry PHP installation.");
                    return;
                }
                installVisualCpp = false;
                cancellation.Token.ThrowIfCancellationRequested();
            }
            await _manager.InstallReleaseAsync(release.Id, progress, cancellation.Token);
            _installProgress = new RuntimeInstallProgress($"Installed {release.DisplayName}", _installProgress?.BytesReceived ?? 0, _installProgress?.TotalBytes);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { throw; }
        catch (Exception exception)
        {
            _visualCppRetryId = exception is VisualCppRuntimeException || installVisualCpp ? release.Id : null;
            _releaseError = _visualCppRetryId is not null ? exception.Message : $"Installation failed: {exception.Message} Retry the official download or import an extracted distribution.";
            _installProgress = new RuntimeInstallProgress("Installation failed");
            var needsVisualCpp = _visualCppRetryId is not null;
            _operationTracker.ConfigureCurrent(retry: () => InstallSelectedReleaseAsync(release, needsVisualCpp));
            throw;
        }
        finally { _installCancellation = null; }
    });

    private string? InstallationBlockReason(RuntimeRelease release)
    {
        if (release.Kind == ServiceKind.Php)
        {
            if (InstalledPhpFor(release) is { } installed && installed.Version == PhpPatch(release.Version))
                return "This release is already active for this channel.";
            if (RetainedPhpFor(release) is not null) return null;
        }
        else if (HasInstallationFor(release))
            return "Already installed. Updates are not available yet.";
        var checksumAvailable = release.Sha256 is { Length: 64 } digest && digest.All(Uri.IsHexDigit);
        if (!checksumAvailable)
            return "This release cannot be verified. You can import an official downloaded runtime instead.";
        return null;
    }

    private PhpInstallation? InstalledPhpFor(RuntimeRelease release) => release.Kind == ServiceKind.Php
        ? _snapshot.PhpInstallations.FirstOrDefault(installation => installation.Channel == ReleaseChannel(release)) : null;

    private PhpRuntimeVersion? RetainedPhpFor(RuntimeRelease release) => release.Kind == ServiceKind.Php
        ? _snapshot.RetainedPhpVersions.FirstOrDefault(version => version.Channel == ReleaseChannel(release) && version.Version == PhpPatch(release.Version)) : null;

    private Task SwitchPhpVersionAsync(PhpRuntimeVersion version) => RunAsync($"Switching PHP {version.Channel} to {version.Version}…", async () =>
    {
        _installProgress = null;
        await _manager.ActivatePhpVersionAsync(version.Channel, version.Version, _lifetime.Token);
    });

    private string InstallActionLabel(RuntimeRelease release) => InstalledPhpFor(release)?.Version == PhpPatch(release.Version) ? "Already active"
        : RetainedPhpFor(release) is { } retained ? $"Switch to PHP {retained.Version}" : InstalledPhpFor(release) is { } installed
        ? $"{(ParsePhpVersion(release.Version) > ParsePhpVersion(installed.Version) ? "Update" : "Switch")} PHP {installed.Channel}"
        : "Download and install";

    private static string ReleaseChannel(RuntimeRelease release) => release.Channel ?? string.Join('.', PhpPatch(release.Version).Split('.').Take(2));
    private static string PhpPatch(string version) => version.Split('-', 2)[0];
    private static Version ParsePhpVersion(string version) => Version.TryParse(PhpPatch(version), out var parsed) ? parsed : new Version();

    private static string FormatBytes(long bytes) => bytes >= 1_048_576 ? $"{bytes / 1_048_576d:0.0} MB" : $"{bytes / 1024d:0.0} KB";

    private Task StartMailAsync() => RunAsync("Starting Mail Trap…", async () =>
    {
        await _manager.StartServiceAsync("mailpit", _lifetime.Token);
        await ReadInboxCoreAsync();
    });

    private async Task ReadInboxCoreAsync(CancellationToken token = default)
    {
        if (!token.CanBeCanceled) token = _lifetime.Token;
        var messages = await _manager.GetMessagesAsync(token);
        token.ThrowIfCancellationRequested();
        await ApplyInboxAsync(messages, token);
    }

    private async Task ApplyInboxAsync(IReadOnlyList<MailMessageSummary> messages, CancellationToken token)
    {
        if (!_messages.SequenceEqual(messages)) _messages = messages;
        var selected = _messages.FirstOrDefault(message => message.Id == _selectedMessageId) ?? _messages.FirstOrDefault();
        if (selected is null)
        {
            _messageCancellation?.Cancel(); _selectedMessageId = null; _loadedMessageId = null; _mailText = string.Empty;
            if (_mailSubject is not null) _mailSubject.Text = "Select a message";
            if (_mailMetadata is not null) _mailMetadata.Text = string.Empty;
            if (_mailPreview is not null) _mailPreview.Text = string.Empty;
        }
        else if (_selectedMessageId != selected.Id || (_loadedMessageId != selected.Id && _messageCancellation is null)) await ReadMessageAsync(selected, token);
        else SetMessageHeader(selected);
    }

    private void UpdatePageRefresh()
    {
        var page = IsVisible && !_shutdown && !_updatesPaused && (_section == "Logs" || (_section == "Mail Trap" && MailIsRunning)) ? _section : null;
        if (page != _autoRefreshPage) { _autoRefreshPage = page; _autoRefreshError = null; }
        _pageRefresher.SetPage(page);
    }

    private async Task RefreshVisiblePageAsync(string page, CancellationToken token)
    {
        if (Busy) return;
        try
        {
            if (page == "Mail Trap")
            {
                var messages = await _manager.GetMessagesAsync(token);
                if (token.IsCancellationRequested || !IsVisible || _section != page || Busy) return;
                await ApplyInboxAsync(messages, token);
            }
            else
            {
                if (_manager.Activity is not null) { UpdateLogOutput(); return; }
                var lines = await _manager.GetLogLinesAsync(token);
                if (token.IsCancellationRequested || !IsVisible || _section != page || Busy) return;
                if (!_logLines.SequenceEqual(lines)) _logLines = lines;
            }
            if (token.IsCancellationRequested || !IsVisible || _section != page) return;
            _autoRefreshError = null;
            Render(_manager.Snapshot);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (token.IsCancellationRequested || !IsVisible || _section != page || _shutdown) return;
            if (_autoRefreshError != exception.Message)
            {
                _autoRefreshError = exception.Message;
                ShowError(exception.Message);
            }
        }
    }

    private void AddUpdateControl()
    {
        Button? updates = null;
        updates = ActionButton(_updatesPaused ? "Resume updates" : "Pause updates", () =>
        {
            _updatesPaused = !_updatesPaused;
            var label = _updatesPaused ? "Resume updates" : "Pause updates";
            updates!.Content = IconLabel(_updatesPaused ? AppIcon.Play : AppIcon.Stop, label, 15);
            AutomationProperties.SetName(updates, label);
            UpdatePageRefresh();
            return Task.CompletedTask;
        }, "SecondaryButtonStyle", _updatesPaused ? AppIcon.Play : AppIcon.Stop, independent: true);
        _pageActions.Children.Add(updates);
    }

    private void UpdateLogOutput(bool force = false)
    {
        if (_updatesPaused && !force) return;
        if (_logOutput is null) return;
        if (_manager.Activity is null) { _logOutput.UpdateLines(_logLines); UpdateActivityServices(_logOutput.Services); return; }
        var today = DateTime.Today;
        var culture = System.Globalization.CultureInfo.CurrentCulture.Name;
        if (_activityVersion == _activity.Version && _activityDate == today && _activityCulture == culture) return;
        _activityVersion = _activity.Version;
        _activityDate = today; _activityCulture = culture;
        var snapshot = _activity.Snapshot;
        UpdateActivityServices(snapshot.Select(entry => entry.Service));
        _logOutput.UpdateEntries(snapshot);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        if (parent is ScrollViewer viewer) return viewer;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(parent, index)) is { } child) return child;
        return null;
    }

    private async Task RunAsync(string label, Func<Task> action)
    {
        if (_operation is not null || _shutdown) return;
        _operation = label;
        Render(_manager.Snapshot);
        using var operation = _operationTracker.Begin(label);
        _foregroundOperation = operation;
        try { await action(); operation.Complete(); _notifications.Publish("Completed", label, NotificationSeverity.Success); }
        catch (OperationCanceledException) { operation.Fail(new OperationCanceledException()); }
        catch (Exception exception) { operation.Fail(exception); ShowError(exception.Message); }
        finally { _foregroundOperation = null; _operation = null; Render(_manager.Snapshot); }
    }

    public void ShowError(string message) => _notifications.Publish("Operation failed", message, NotificationSeverity.Error);
    public void BeginShutdown()
    {
        _shutdown = true;
        _pageRefresher.Dispose(); _statusBar.Dispose(); _notifications.Published -= OnNotification;
        CancelReleaseCheck();
        _lifetime.Cancel();
        _installCancellation?.Cancel();
        foreach (var owned in OwnedWindows.Cast<Window>().ToArray()) owned.Close();
        IsEnabled = false;
        _operationText.Text = "Stopping managed services before exit…";
    }
    public void CloseForExit() { _closeForExit = true; Close(); }
    private void OnClosing(object? sender, CancelEventArgs args) { if (!_closeForExit) { args.Cancel = true; Hide(); } }
    private bool Busy => _shutdown || _operation is not null || _snapshot.IsBusy || _snapshot.Services.Any(s => IsTransition(s.Status));

    private IDisposable HoldFocus()
    {
        _focusHold++;
        return new Scope(() => { _focusHold--; if (IsVisible) Activate(); });
    }

    private Task OpenPathAsync(string path)
    {
        _ignoreDeactivationUntil = Environment.TickCount64 + 1500;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { _ignoreDeactivationUntil = 0; throw; }
        return Task.CompletedTask;
    }

    private Task OpenEditorAsync(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("This configuration file has not been created yet. Add the runtime first.", path);
        return OpenPathAsync(path);
    }


    private Button ActionButton(string label, Func<Task> action, string style = "SecondaryButtonStyle", AppIcon? icon = null, string? accessibleName = null, bool independent = false, Func<bool>? enabled = null)
    {
        var button = new Button { Content = icon is { } glyph ? string.IsNullOrEmpty(label) ? UiIcon.Create(glyph, 17) : IconLabel(glyph, label, 15) : label, Margin = new Thickness(0, 0, 6, 0) };
        if (_section == "Updates" && _releaseContent is not null) button.Tag = "ReleaseStage";
        button.SetResourceReference(StyleProperty, style); AutomationProperties.SetName(button, accessibleName ?? label);
        if (string.IsNullOrEmpty(label) && accessibleName is not null) button.ToolTip = accessibleName;
        Func<bool> capability = enabled ?? (() => !_shutdown && (independent || !Busy));
        _bodyButtons.Add((button, capability)); button.IsEnabled = capability();
        button.Click += async (_, _) => { try { await action(); } catch (Exception exception) { ShowError(exception.Message); } };
        return button;
    }

    private Button MoreButton(string accessibleName, Func<IEnumerable<MenuItem>> items) => ActionButton("", () =>
    {
        var menu = new ContextMenu(); foreach (var item in items()) menu.Items.Add(item);
        _focusHold++; menu.Closed += (_, _) => _focusHold--;
        menu.Placement = PlacementMode.Bottom;
        menu.PlacementTarget = Keyboard.FocusedElement as UIElement;
        menu.IsOpen = true;
        return Task.CompletedTask;
    }, "IconButtonStyle", AppIcon.More, accessibleName, independent: true);

    private MenuItem Menu(string label, Func<Task> action, bool enabled = true)
    {
        var item = new MenuItem { Header = label, IsEnabled = enabled };
        item.Click += async (_, _) => { try { await action(); } catch (Exception exception) { ShowError(exception.Message); } };
        return item;
    }

    private UIElement ServiceControl(ServiceKind kind, string title)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 13) };
        var status = T(string.Empty, 11, muted: true); status.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(status);
        var service = _snapshot.Services.FirstOrDefault(s => s.Kind == kind && s.Status != ServiceStatus.NotInstalled);
        status.Text = $"{title}  ·  {StatusLabel(service?.Status ?? ServiceStatus.NotInstalled)}";
        _inlineServices[kind] = new InlineService(title, status);
        return row;
    }

    private Task ToggleServiceAsync(ServiceKind kind) => RunAsync("Updating service…", async () =>
    {
        var service = _snapshot.Services.FirstOrDefault(s => s.Kind == kind && s.Status != ServiceStatus.NotInstalled) ?? throw new InvalidOperationException("Install this runtime first.");
        if (service.Status == ServiceStatus.Running) await _manager.StopServiceAsync(service.Id, _lifetime.Token);
        else
        {
            await _manager.StartServiceAsync(service.Id, _lifetime.Token);
            if (kind == ServiceKind.Mailpit) await ReadInboxCoreAsync();
        }
    });

    private void Heading(string title, string description) { _pageTitle.Text = title; _pageDescription.Text = description; }
    private bool HasRuntime(ServiceKind kind) => _snapshot.Services.Any(s => s.Kind == kind && s.Status != ServiceStatus.NotInstalled);
    private bool MailIsRunning => _snapshot.Services.Any(service => service.Kind == ServiceKind.Mailpit && service.Status == ServiceStatus.Running);
    private bool HasInstallationFor(RuntimeRelease release) => release.Kind == ServiceKind.Php
        ? InstalledPhpFor(release)?.Version == PhpPatch(release.Version)
        : release.Kind == ServiceKind.FrankenPhp ? _snapshot.Services.Any(s => s.Id == "franken-v" + release.Version.Replace('.', '-') && s.Status != ServiceStatus.NotInstalled) : HasRuntime(release.Kind);
    private bool AnyRunning => _snapshot.Services.Any(s => s.Status is ServiceStatus.Running or ServiceStatus.Starting or ServiceStatus.Stopping);
    private ServiceSnapshot? PhpService(PhpInstallation installation) => _snapshot.Services.FirstOrDefault(s => s.Kind == ServiceKind.Php && s.Version == installation.Version) ?? _snapshot.Services.FirstOrDefault(s => s.Kind == ServiceKind.Php && s.Id.Contains(installation.Channel, StringComparison.Ordinal));
    private static bool IsTransition(ServiceStatus status) => status is ServiceStatus.Starting or ServiceStatus.Stopping or ServiceStatus.Restarting or ServiceStatus.Updating;
    private static string StatusLabel(ServiceStatus status) => status switch { ServiceStatus.NotInstalled => "Not installed", ServiceStatus.Starting => "Starting…", ServiceStatus.Stopping => "Stopping…", ServiceStatus.Restarting => "Restarting…", ServiceStatus.Updating => "Updating…", ServiceStatus.Failed => "Needs attention", _ => status.ToString() };
    private static string StatusBrush(ServiceStatus status) => status == ServiceStatus.Running ? "SuccessBrush" : status == ServiceStatus.Failed ? "ErrorBrush" : IsTransition(status) ? "WarningBrush" : "MutedBrush";

    private static StackPanel IconLabel(AppIcon icon, string label, double iconSize = 17)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var glyph = UiIcon.Create(icon, iconSize); glyph.Margin = new Thickness(0, 0, 8, 0); panel.Children.Add(glyph);
        panel.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }); return panel;
    }

    private static FrameworkElement IconTile(AppIcon icon, double size)
    {
        var tile = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(9), Child = UiIcon.Create(icon, size * .51, "AccentBrush"), VerticalAlignment = VerticalAlignment.Center };
        tile.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush"); return tile;
    }

    private UIElement Empty(AppIcon icon, string title, string description, string primary, Func<Task> action, string? secondary = null, Func<Task>? secondaryAction = null, string? steps = null, AppIcon primaryIcon = AppIcon.Plus)
    {
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 360, Margin = new Thickness(12, 20, 12, 26) };
        var mark = IconTile(icon, 50); mark.HorizontalAlignment = HorizontalAlignment.Center; panel.Children.Add(mark);
        var heading = T(title, 20, true, margin: new Thickness(0, 18, 0, 9)); heading.TextAlignment = TextAlignment.Center; panel.Children.Add(heading);
        var detail = T(description, 12, muted: true); detail.TextAlignment = TextAlignment.Center; detail.LineHeight = 20; panel.Children.Add(detail);
        var actions = new WrapPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 21, 0, 0) };
        var cta = ActionButton(primary, action, "PrimaryButtonStyle", primaryIcon); cta.HorizontalAlignment = HorizontalAlignment.Center; cta.Margin = new Thickness(0, 0, 0, 6); actions.Children.Add(cta);
        if (secondary is not null && secondaryAction is not null) { var link = ActionButton(secondary, secondaryAction, "QuietButtonStyle"); link.HorizontalAlignment = HorizontalAlignment.Center; link.Margin = new Thickness(0); actions.Children.Add(link); }
        panel.Children.Add(actions);
        if (steps is not null) { var footer = T(steps, 10, muted: true, margin: new Thickness(0, 25, 0, 0)); footer.TextAlignment = TextAlignment.Center; panel.Children.Add(footer); }
        var container = new Grid(); container.Children.Add(panel);
        var viewport = Scroll(container); viewport.Tag = "EmptyStateScroll";
        AutomationProperties.SetName(viewport, title);
        viewport.SizeChanged += (_, _) =>
        {
            container.MinHeight = Math.Max(0, viewport.ActualHeight - viewport.Padding.Top - viewport.Padding.Bottom);
            var compact = viewport.ActualHeight < 320;
            mark.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            panel.Margin = compact ? new Thickness(12, 8, 12, 8) : new Thickness(12, 20, 12, 26);
            heading.Margin = compact ? new Thickness(0, 0, 0, 8) : new Thickness(0, 18, 0, 9);
            actions.Orientation = compact ? Orientation.Horizontal : Orientation.Vertical;
            actions.Margin = new Thickness(0, compact ? 12 : 21, 0, 0);
            cta.Margin = compact ? new Thickness(0, 0, 6, 0) : new Thickness(0, 0, 0, 6);
        };
        return viewport;
    }

    private UIElement InlineNotice(string text, string? action = null, Func<Task>? handler = null)
    {
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        row.Children.Add(new Wpf.Ui.Controls.InfoBar { Message = text, IsOpen = true, IsClosable = false });
        if (action is not null && handler is not null)
        {
            var button = ActionButton(action, handler, "SecondaryButtonStyle");
            button.Margin = new Thickness(0, 8, 0, 0); row.Children.Add(button);
        }
        return row;
    }

    private static Border Badge(string label, string background, Thickness margin)
    {
        var badge = new Border { Child = T(label, 9), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 3, 6, 3), Margin = margin, VerticalAlignment = VerticalAlignment.Center };
        badge.SetResourceReference(Border.BackgroundProperty, background); return badge;
    }
    private static Wpf.Ui.Controls.Card RowSurface(UIElement content) { var row = Surface(content, "CardBrush", 9, new Thickness(13, 13, 9, 13)); row.Margin = new Thickness(0, 0, 0, 9); return row; }
    private static Wpf.Ui.Controls.Card Surface(UIElement content, string background, double radius, Thickness padding)
    {
        var border = new Wpf.Ui.Controls.Card { Content = content, Padding = padding, BorderThickness = new Thickness(1), HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        border.SetResourceReference(Control.BackgroundProperty, background); border.SetResourceReference(Control.BorderBrushProperty, "BorderBrush"); return border;
    }
    private static ScrollViewer Scroll(UIElement content) => new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 3, 4) };
    private static TextBlock T(string text, double size = 13, bool bold = false, bool muted = false, Thickness? margin = null)
    {
        var block = new TextBlock { Text = text, FontSize = Math.Max(12, size), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = margin ?? new Thickness(0) };
        block.SetResourceReference(TextBlock.ForegroundProperty, muted ? "MutedBrush" : "TextBrush"); return block;
    }
    private sealed record PhpRow(string Id, TextBlock Status, Button Toggle, TextBlock Metadata, Border Default);
    private sealed record InlineService(string Title, TextBlock Status);
    private sealed class Scope(Action dispose) : IDisposable { public void Dispose() => dispose(); }
}
