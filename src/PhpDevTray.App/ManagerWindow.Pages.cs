using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using PhpDevTray.Core;
using Button = Wpf.Ui.Controls.Button;
namespace PhpDevTray.App;
internal sealed partial class ManagerWindow
{
    private void UpdateSettings()
    {
        _updatingInputs = true;
        if (_appearancePicker is not null) _appearancePicker.SelectedItem = _snapshot.Theme;
        if (_pathSetting is not null) { _pathSetting.IsChecked = _snapshot.PathIntegrationEnabled; _pathSetting.IsEnabled = !Busy && _snapshot.PhpInstallations.Count > 0; }
        if (_startupSetting is not null)
        {
            _startupSetting.IsChecked = _startupState?.Enabled == true;
            _startupSetting.IsEnabled = !Busy && !_startupChanging && _startupState is not null;
        }
        if (_startupNotice is not null)
        {
            _startupNotice.Text = _startupState?.Notice ?? string.Empty;
            _startupNotice.Visibility = string.IsNullOrEmpty(_startupNotice.Text) ? Visibility.Collapsed : Visibility.Visible;
        }
        if (_defaultPhpPicker is not null) { _defaultPhpPicker.SelectedItem = _snapshot.DefaultPhpChannel; _defaultPhpPicker.IsEnabled = !Busy; }
        _updatingInputs = false;
    }

    private async Task ReadStartupSettingAsync()
    {
        try
        {
            var state = await _startupRegistration.ReadAsync();
            if (!_startupChanging) _startupState = state;
        }
        catch (Exception exception) { _startupState = null; ShowError("Could not read Windows startup settings. " + exception.Message); }
        if (!_shutdown) Render(_manager.Snapshot);
    }

    internal async Task PrepareSmokeSectionAsync(string section, bool checkReleases = false)
    {
        SetSmokeSection(section);
        if (section is "Mail Trap" or "Logs") { await _pageRefresher.RefreshNowAsync(); await _pageRefresher.RefreshNowAsync(); }
        else if (section == "Updates" && checkReleases) await CheckReleasesAsync();
    }

    internal Task PreparePhpUpdatesForSmokeAsync(string channel) => OpenPhpUpdatesAsync(channel);

    internal void SetMailSearchForSmoke(string query)
    {
        if (_mailSearch is null) throw new InvalidOperationException("Open a nonempty running inbox before testing search.");
        _mailSearch.Text = query;
    }


    private IEnumerable<MenuItem> PhpMenu(PhpInstallation installation)
    {
        if (PhpService(installation) is { } service) yield return Menu("Restart", () => RunAsync($"Restarting PHP {installation.Channel}…", () => _manager.RestartServiceAsync(service.Id, _lifetime.Token)), !Busy);
        if (_snapshot.DefaultPhpChannel != installation.Channel) yield return Menu("Use as CLI default", () => RunAsync("Changing default PHP…", () => _manager.SetDefaultPhpAsync(installation.Channel, _lifetime.Token)), !Busy);
        yield return Menu("Check for updates…", () => OpenPhpUpdatesAsync(installation.Channel), !Busy);
        var retained = _snapshot.RetainedPhpVersions.Where(version => version.Channel == installation.Channel)
            .OrderByDescending(version => ParsePhpVersion(version.Version)).ToArray();
        if (retained.Length > 0)
        {
            var versions = new MenuItem { Header = "Previous patches", IsEnabled = !Busy };
            AutomationProperties.SetName(versions, $"Previous patches for PHP {installation.Channel}");
            foreach (var version in retained)
            {
                var option = Menu($"Switch to PHP {version.Version}", () => SwitchPhpVersionAsync(version), !Busy);
                option.ToolTip = "Keep this channel's port, extensions, and CLI selection. A running channel briefly restarts.";
                versions.Items.Add(option);
            }
            yield return versions;
        }
        yield return Menu("Manage extensions…", () => ShowExtensionsAsync(installation.Channel), !Busy);
        yield return Menu("Edit PHP configuration", () => OpenEditorAsync(Path.Combine(_snapshot.DataDirectory, "user", "php", installation.Channel, "conf.d", "20-custom.ini")));
        yield return Menu("Open runtime folder", () => OpenPathAsync(installation.Directory));
    }


    private Task NavigateAsync(string section) { _shellNavigation.Navigate(section); return _navigationTask; }
    private Task ToggleAllPhpAsync() => RunAsync("Updating PHP runtimes…", async () =>
    {
        var services = _snapshot.Services.Where(s => s.Kind is ServiceKind.Php or ServiceKind.FrankenPhp && s.Status != ServiceStatus.NotInstalled).ToArray();
        var stop = services.Any(s => s.Status == ServiceStatus.Running);
        foreach (var service in services)
            if (stop) await _manager.StopServiceAsync(service.Id, _lifetime.Token); else await _manager.StartServiceAsync(service.Id, _lifetime.Token);
    });
    private static TextBlock RuntimeMetadata(string text)
    {
        var block = T(text, 12, muted: true, margin: new Thickness(0, 4, 0, 0));
        block.Height = 20; block.TextWrapping = TextWrapping.NoWrap; block.TextTrimming = TextTrimming.CharacterEllipsis;
        return block;
    }
    private static Wpf.Ui.Controls.Card RuntimeRow(StackPanel labels, FrameworkElement controls, AppIcon? icon = null)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new Grid(); heading.ColumnDefinitions.Add(new ColumnDefinition()); heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = (TextBlock)labels.Children[0]; labels.Children.RemoveAt(0);
        title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis; title.ToolTip = title.Text;
        title.VerticalAlignment = VerticalAlignment.Center; title.MinHeight = 24;
        heading.Children.Add(title);
        controls.VerticalAlignment = VerticalAlignment.Top; controls.HorizontalAlignment = HorizontalAlignment.Right; controls.Margin = new Thickness(16, 0, 0, 0);
        Grid.SetColumn(controls, 1); heading.Children.Add(controls); grid.Children.Add(heading);
        labels.Margin = new Thickness(0, 4, 0, 0); Grid.SetRow(labels, 1); grid.Children.Add(labels);
        if (icon is null) return RowSurface(grid);
        var outer = new Grid(); outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) }); outer.ColumnDefinitions.Add(new ColumnDefinition());
        var glyph = UiIcon.Create(icon.Value, 24); glyph.VerticalAlignment = VerticalAlignment.Top; glyph.Margin = new Thickness(0, 4, 12, 0); outer.Children.Add(glyph);
        Grid.SetColumn(grid, 1); outer.Children.Add(grid);
        return RowSurface(outer);
    }
    private void BuildPhp()
    {
        Heading("PHP", "Manage PHP NTS and FrankenPHP runtimes.");
        _pageActions.Children.Add(ActionButton("Install PHP", () => OpenInstallAsync(ServiceKind.Php), "PrimaryButtonStyle", AppIcon.Plus, independent: true));
        _pageActions.Children.Add(ActionButton("Open config folder", () => OpenPathAsync(Path.Combine(_snapshot.DataDirectory, "user", "php")), icon: AppIcon.Folder, independent: true));
        var rows = new StackPanel();
        foreach (var service in _snapshot.Services.Where(s => s.Kind is ServiceKind.Php or ServiceKind.FrankenPhp && s.Status != ServiceStatus.NotInstalled))
        {
            var id = service.Id; var labels = new StackPanel();
            labels.Children.Add(T(service.DisplayName, 16, true));
            var metadata = RuntimeMetadata(""); var status = RuntimeMetadata(""); labels.Children.Add(metadata); labels.Children.Add(status);
            var badge = Badge("CLI default", "AccentSoftBrush", new Thickness(0, 4, 0, 0)); badge.HorizontalAlignment = HorizontalAlignment.Left; labels.Children.Add(badge);
            var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var toggle = ActionButton("Start", () => RunAsync("Updating PHP…", () => ServiceIsRunning(id) ? _manager.StopServiceAsync(id, _lifetime.Token) : _manager.StartServiceAsync(id, _lifetime.Token)));
            controls.Children.Add(toggle);
            controls.Children.Add(RestartButton(() => RestartServiceAsync(id, service.DisplayName), () => ServiceIsRunning(id), "Restart " + service.DisplayName));
            controls.Children.Add(MoreButton(service.DisplayName + " actions", () => RuntimeMenu(id)));
            _phpRows[id] = new(id, status, toggle, metadata, badge); rows.Children.Add(RuntimeRow(labels, controls));
        }
        if (_phpRows.Count == 0) rows.Children.Add(T("Install PHP NTS or FrankenPHP to get started.", muted: true));
        _contentHost.Children.Add(Scroll(rows));
    }
    private IEnumerable<MenuItem> RuntimeMenu(string id)
    {
        if (_snapshot.PhpInstallations.FirstOrDefault(item => item.Id == id) is { } php) return PhpMenu(php);
        return [Menu("Restart", () => RunAsync("Restarting PHP…", () => _manager.RestartServiceAsync(id, _lifetime.Token)), !Busy),
            Menu("Manage extensions…", () => ShowExtensionsAsync(id), !Busy),
            Menu("Manage sites", () => NavigateAsync("Sites")),
            Menu("Open workspace", () => OpenPathAsync(_snapshot.DataDirectory))];
    }
    private void UpdatePhpRow(PhpRow row)
    {
        var service = _snapshot.Services.FirstOrDefault(s => s.Id == row.Id); if (service is null) return;
        row.Status.Text = service.Detail ?? StatusLabel(service.Status); row.Status.ToolTip = row.Status.Text;
        row.Status.SetResourceReference(TextBlock.ForegroundProperty, StatusBrush(service.Status));
        row.Metadata.Text = service.Endpoint ?? (service.Kind == ServiceKind.Php ? "PHP NTS" : "FrankenPHP");
        row.Default.Visibility = service.IsDefault || _snapshot.PhpInstallations.Any(p => p.Id == row.Id && p.Channel == _snapshot.DefaultPhpChannel) ? Visibility.Visible : Visibility.Hidden;
        row.Toggle.Content = IconLabel(service.Status == ServiceStatus.Running ? AppIcon.Stop : AppIcon.Play, service.Status == ServiceStatus.Running ? "Stop" : "Start", 14);
        AutomationProperties.SetName(row.Toggle, $"{(service.Status == ServiceStatus.Running ? "Stop" : "Start")} {service.DisplayName}");
    }
    private TextBlock? _caddyStatus, _caddyEndpoint, _caddyCount, _caddyFailure;
    private Button? _caddyAction;
    private void BuildCaddy()
    {
        Heading("Web server", "Caddy serves local sites over HTTPS.");
        if (!HasRuntime(ServiceKind.Caddy))
        { _contentHost.Children.Add(Empty(AppIcon.Server, "Install Caddy", "Caddy routes local sites to PHP.", "Install Caddy", () => OpenInstallAsync(ServiceKind.Caddy), "Import Caddy", () => BrowseRuntimeAsync(ServiceKind.Caddy))); return; }
        var rows = new StackPanel(); var labels = new StackPanel(); labels.Children.Add(T("Caddy", 16, true));
        _caddyStatus = RuntimeMetadata(""); _caddyEndpoint = RuntimeMetadata(""); _caddyCount = RuntimeMetadata(""); _caddyFailure = RuntimeMetadata("");
        labels.Children.Add(_caddyStatus); labels.Children.Add(_caddyEndpoint); labels.Children.Add(_caddyCount); labels.Children.Add(_caddyFailure);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _caddyAction = ActionButton("Start", () => ToggleServiceAsync(ServiceKind.Caddy), enabled: () => !Busy && (ServiceIsRunning("caddy") || _snapshot.Sites.Any(site => site.Enabled))); controls.Children.Add(_caddyAction);
        controls.Children.Add(RestartButton(() => RestartServiceAsync("caddy", "Caddy"), () => ServiceIsRunning("caddy"), "Restart Caddy"));
        controls.Children.Add(MoreButton("Caddy actions", () => [
            Menu("Restart", () => RunAsync("Restarting Caddy…", () => _manager.RestartServiceAsync("caddy", _lifetime.Token)), !Busy && ServiceIsRunning("caddy")),
            Menu("Manage sites", () => NavigateAsync("Sites")),
            Menu("Edit workspace settings", () => OpenEditorAsync(Path.Combine(_snapshot.DataDirectory, "settings.json"))),
            Menu("View activity", () => NavigateAsync("Logs")),
            Menu("Open workspace", () => OpenPathAsync(_snapshot.DataDirectory))]));
        rows.Children.Add(RuntimeRow(labels, controls)); _contentHost.Children.Add(Scroll(rows));
    }
    private void UpdateCaddy()
    {
        var service = _snapshot.Services.FirstOrDefault(s => s.Kind == ServiceKind.Caddy); if (_caddyStatus is null || service is null) return;
        _caddyStatus.Text = StatusLabel(service.Status); _caddyStatus.SetResourceReference(TextBlock.ForegroundProperty, StatusBrush(service.Status));
        _caddyEndpoint!.Text = service.Endpoint ?? "HTTPS for local sites"; _caddyCount!.Text = $"{_snapshot.Sites.Count(site => site.Enabled)} enabled sites";
        _caddyFailure!.Text = service.Detail ?? ""; _caddyFailure.ToolTip = _caddyFailure.Text;
        _caddyAction!.Content = IconLabel(service.Status == ServiceStatus.Running ? AppIcon.Stop : AppIcon.Play, service.Status == ServiceStatus.Running ? "Stop" : "Start", 14);
    }
    private ComboBox? _activityService;
    private TextBox? _activitySearch;
    private sealed record ActivityServiceChoice(string Id, string Label);
    private long _activityVersion = -1;
    private DateTime _activityDate;
    private string? _activityCulture;
    private string _activityServiceName = "All services";
    private void BuildLogs()
    {
        Heading("Activity", "Live diagnostics from all services."); AddUpdateControl();
        _pageActions.Children.Add(ActionButton("Clear", () => { _logOutput?.Select(0, 0); _activity.Clear(); _logLines = []; _activityVersion = -1; UpdateLogOutput(force: true); return Task.CompletedTask; }, independent: true));
        _pageActions.Children.Add(ActionButton("Open logs", () => OpenPathAsync(Path.Combine(_snapshot.DataDirectory, "logs")), independent: true));
        var panel = new DockPanel();
        _activityService = new ComboBox { ItemsSource = new[] { new ActivityServiceChoice("All services", "All services") }, DisplayMemberPath = nameof(ActivityServiceChoice.Label), SelectedValuePath = nameof(ActivityServiceChoice.Id), SelectedIndex = 0, MinWidth = 160, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
        AutomationProperties.SetName(_activityService, "Filter activity by service");
        _activityService.SelectionChanged += (_, _) => { _activityServiceName = _activityService.SelectedValue as string ?? "All services"; if (_logOutput is not null) _logOutput.ServiceFilter = _activityServiceName == "All services" ? null : _activityServiceName; };
        DockPanel.SetDock(_activityService, Dock.Top); panel.Children.Add(_activityService);
        var searchRow = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        searchRow.ColumnDefinitions.Add(new ColumnDefinition()); searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _activitySearch = new Wpf.Ui.Controls.TextBox { PlaceholderText = "Search activity", MinHeight = 32 };
        AutomationProperties.SetName(_activitySearch, "Search activity");
        AutomationProperties.SetHelpText(_activitySearch, "Enter finds the next match. Shift Enter finds the previous match.");
        _activitySearch.TextChanged += (_, _) => _logOutput?.Search(_activitySearch.Text);
        _activitySearch.PreviewKeyDown += (_, args) =>
        {
            if (args.Key != System.Windows.Input.Key.Enter) return;
            _logOutput?.FindMatch(System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift)); args.Handled = true;
        };
        searchRow.Children.Add(_activitySearch);
        var searchActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
        var previous = ActionButton("", () => { _logOutput?.FindMatch(previous: true); return Task.CompletedTask; }, "IconButtonStyle", AppIcon.ChevronDown, "Previous match", independent: true);
        ((FrameworkElement)previous.Content).RenderTransformOrigin = new Point(.5, .5);
        ((FrameworkElement)previous.Content).RenderTransform = new System.Windows.Media.RotateTransform(180);
        searchActions.Children.Add(previous);
        searchActions.Children.Add(ActionButton("", () => { _logOutput?.FindMatch(); return Task.CompletedTask; }, "IconButtonStyle", AppIcon.ChevronDown, "Next match", independent: true));
        Grid.SetColumn(searchActions, 1); searchRow.Children.Add(searchActions);
        DockPanel.SetDock(searchRow, Dock.Top); panel.Children.Add(searchRow);
        _logOutput = new LogViewer(); panel.Children.Add(_logOutput); _activityVersion = -1;
        _logOutput.SearchRequested += (_, _) => { _activitySearch.Focus(); _activitySearch.SelectAll(); };
        _contentHost.Children.Add(panel); UpdateLogOutput();
    }
    private void UpdateActivityServices(IEnumerable<string> services)
    {
        if (_activityService is null) return;
        ActivityServiceChoice[] choices = [new("All services", "All services"),
            .. services.Distinct().Select(id => new ActivityServiceChoice(id, id == "application" ? "PHP DevTray" : id)).OrderBy(item => item.Label)];
        if ((_activityService.ItemsSource as ActivityServiceChoice[] ?? []).SequenceEqual(choices)) return;
        var selected = _activityServiceName;
        _activityService.ItemsSource = choices;
        _activityService.SelectedValue = choices.Any(item => item.Id == selected) ? selected : "All services";
    }
    private string ReleaseTypeName() => _releaseKind switch { ServiceKind.Php => "PHP NTS", ServiceKind.FrankenPhp => "FrankenPHP", ServiceKind.Caddy => "Caddy", _ => "Mailpit" };
    private sealed record RuntimeChoice(ServiceKind Kind, string Label);
    private void BuildInstall()
    {
        Heading(_releasePhpChannel is { } channel ? $"Update PHP {channel}" : "Install runtimes", "Install a verified release or import an extracted distribution.");
        var panel = new DockPanel();
        RuntimeChoice[] kinds = [new(ServiceKind.Php, "PHP NTS"), new(ServiceKind.FrankenPhp, "FrankenPHP"), new(ServiceKind.Caddy, "Caddy"), new(ServiceKind.Mailpit, "Mailpit")];
        _runtimeSelector = new TabControl { Margin = new Thickness(0, 0, 0, 16) };
        _runtimeSelector.SetResourceReference(StyleProperty, "RuntimeTabsStyle");
        _runtimeSelector.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "RuntimeTabStyle");
        foreach (var choice in kinds)
        {
            var tab = new TabItem { Header = choice.Label, Tag = choice.Kind };
            AutomationProperties.SetName(tab, choice.Label);
            _runtimeSelector.Items.Add(tab);
            if (choice.Kind == _releaseKind) _runtimeSelector.SelectedItem = tab;
        }
        AutomationProperties.SetName(_runtimeSelector, "Runtime to install");
        _runtimeSelector.SelectionChanged += async (_, args) =>
        {
            if (args.Source != _runtimeSelector || _runtimeSelector.SelectedItem is not TabItem { Tag: ServiceKind kind } || kind == _releaseKind) return;
            ResetReleaseSelection(kind);
            _renderedSection = _section;
            Heading("Install runtimes", "Install a verified release or import an extracted distribution.");
            Render(_manager.Snapshot);
            await CheckReleasesAsync();
        };
        DockPanel.SetDock(_runtimeSelector, Dock.Top); panel.Children.Add(_runtimeSelector);
        _releaseContent = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top };
        panel.Children.Add(Scroll(_releaseContent)); _contentHost.Children.Add(panel); UpdateInstall();
    }
    private TextBlock? _releaseReason;
    private Button? _installAction, _findReleases;
    private bool _updatingReleaseInputs;
    private void UpdateInstall()
    {
        if (_releaseContent is null) return;
        if (_releaseContent.Content is null)
        {
            var form = new StackPanel();
            _findReleases = ActionButton("Find releases", CheckReleasesAsync, independent: true); _findReleases.HorizontalAlignment = HorizontalAlignment.Left; form.Children.Add(_findReleases);
            _releaseVersionPicker = new ComboBox { DisplayMemberPath = nameof(RuntimeRelease.DisplayName), Margin = new Thickness(0, 16, 0, 0), MinHeight = 32 };
            AutomationProperties.SetName(_releaseVersionPicker, "Release version");
            _releaseVersionPicker.SelectionChanged += (_, _) => { if (!_updatingReleaseInputs) { _selectedReleaseId = (_releaseVersionPicker.SelectedItem as RuntimeRelease)?.Id; UpdateInstall(); } };
            form.Children.Add(_releaseVersionPicker);
            _releaseReason = T("", muted: true, margin: new Thickness(0, 12, 0, 8)); _releaseReason.MinHeight = 40; form.Children.Add(_releaseReason);
            _installAction = ActionButton("Install", async () => { if (_releaseVersionPicker.SelectedItem is RuntimeRelease release) { if (RetainedPhpFor(release) is { } retained) await SwitchPhpVersionAsync(retained); else await InstallSelectedReleaseAsync(release); } }, enabled: () => !Busy && _releaseVersionPicker?.SelectedItem is RuntimeRelease release && InstallationBlockReason(release) is null);
            _installAction.HorizontalAlignment = HorizontalAlignment.Left; form.Children.Add(_installAction);
            form.Children.Add(T("Import an extracted distribution or install a verified release.", 12, muted: true, margin: new Thickness(0, 24, 0, 8)));
            var import = ActionButton("Import extracted runtime…", () => BrowseRuntimeAsync(_releaseKind), icon: AppIcon.Folder); import.HorizontalAlignment = HorizontalAlignment.Left; form.Children.Add(import);
            _releaseContent.Content = form;
        }
        _updatingReleaseInputs = true;
        try
        {
            if (!ReferenceEquals(_releaseVersionPicker!.ItemsSource, _releases)) _releaseVersionPicker.ItemsSource = _releases;
            _releaseVersionPicker.SelectedItem = _releases.FirstOrDefault(r => r.Id == _selectedReleaseId) ?? _releases.FirstOrDefault();
        }
        finally { _updatingReleaseInputs = false; }
        var selected = _releaseVersionPicker.SelectedItem as RuntimeRelease;
        _findReleases!.IsEnabled = _releaseCheck is null;
        _releaseReason!.Text = _releaseCheck is not null ? $"Fetching {ReleaseTypeName()} releases…"
            : selected is null ? _releaseError ?? (_releaseCheckComplete ? "No compatible releases found" : "Find available releases to choose a version.")
            : InstallationBlockReason(selected) ?? "";
        _installAction!.Content = selected is null ? "Install" : InstallActionLabel(selected);
        _installAction.IsEnabled = !Busy && selected is not null && InstallationBlockReason(selected) is null;
        _releaseVersionPicker.IsEnabled = _releaseCheck is null && _releases.Count > 0;
    }
}
