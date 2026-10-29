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

internal sealed class ManagerWindow : Window
{
    private static readonly string[] Sections = ["PHP Versions", "Nginx Sites", "Mail Trap", "Logs", "Updates", "Settings"];
    private readonly IEnvironmentManager _manager;
    private readonly TextBlock _overall, _operationText, _errorText, _pageTitle, _pageDescription;
    private readonly Button _globalAction;
    private readonly Button _environmentRefresh;
    private readonly Border _errorBanner;
    private readonly Grid _contentHost;
    private readonly StackPanel _pageActions;
    private readonly Dictionary<string, Button> _navigation = new();
    private readonly Dictionary<ServiceKind, TextBlock> _serviceLabels = new();
    private readonly Dictionary<ServiceKind, Border> _serviceDots = new();
    private readonly Dictionary<ServiceKind, Button> _serviceButtons = new();
    private readonly Dictionary<ServiceKind, Button> _serviceToggles = new();
    private readonly Dictionary<ServiceKind, Border> _serviceCards = new();
    private readonly List<FrameworkElement> _serviceGlyphs = [];
    private readonly List<ComboBox> _siteVersionPickers = [];
    private readonly Dictionary<string, PhpRow> _phpRows = new();
    private readonly Dictionary<ServiceKind, InlineService> _inlineServices = new();
    private readonly List<(Button Button, Func<bool> Enabled)> _bodyButtons = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly PageAutoRefresher _pageRefresher;
    private string? _autoRefreshPage, _autoRefreshError;
    private EnvironmentSnapshot _snapshot;
    private string _section, _bodySignature = string.Empty;
    private string? _renderedSection, _error, _dismissedError, _operation;
    private int _focusHold;
    private long _ignoreDeactivationUntil;
    private bool _closeForExit, _shutdown, _updatingInputs;
    private IReadOnlyList<MailMessageSummary> _messages = [];
    private IReadOnlyList<MailMessageSummary>? _renderedMessages;
    private string _mailText = string.Empty;
    private IReadOnlyList<string> _logLines = [];
    private string? _selectedMessageId, _loadedMessageId;
    private ListBox? _mailList;
    private TextBox? _mailSearch, _mailPreview, _logOutput;
    private TextBlock? _mailSubject, _mailMetadata;
    private TextBlock? _mailResults;
    private Border? _mailNoResults;
    private Grid? _mailDetail;
    private string? _feedbackMessage;
    private CancellationTokenSource? _messageCancellation;
    private ServiceKind _releaseKind = ServiceKind.Php;
    private string? _releasePhpChannel;
    private IReadOnlyList<RuntimeRelease> _releases = [];
    private string? _selectedReleaseId, _releaseError;
    private bool _releaseCheckComplete;
    private CancellationTokenSource? _installCancellation;
    private RuntimeInstallProgress? _installProgress;
    private long _lastProgressRender;
    private ContentControl? _releaseContent;
    private ComboBox? _releaseVersionPicker;
    private string _releaseSignature = string.Empty;
    private TextBlock? _installPhase, _installBytes;
    private ProgressBar? _downloadBar;
    private Border? _downloadPanel;
    private Button? _cancelDownload;
    private readonly Dictionary<ThemePreference, Button> _themeButtons = new();
    private CheckBox? _pathSetting;
    private ComboBox? _defaultPhpPicker;
    private WindowState _restoreState = WindowState.Normal;

    public ManagerWindow(IEnvironmentManager manager)
    {
        _manager = manager; _snapshot = manager.Snapshot;
        _section = Sections.Contains(_snapshot.LastSection) ? _snapshot.LastSection : Sections[0];
        Title = "PHP DevTray"; Width = 1000; Height = 740; MinWidth = 560; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true; WindowStyle = WindowStyle.SingleBorderWindow; ResizeMode = ResizeMode.CanResize;
        AllowsTransparency = false; Topmost = false; SetResourceReference(BackgroundProperty, "SurfaceBrush");
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(184) });
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        var shell = Surface(layout, "SurfaceBrush", 14, new Thickness(0));
        shell.ClipToBounds = true; Content = shell;

        var sidebar = new DockPanel { LastChildFill = true, Margin = new Thickness(12, 21, 12, 13) };
        var sidebarSurface = new Border { Child = sidebar, CornerRadius = new CornerRadius(14, 0, 0, 14), BorderThickness = new Thickness(0, 0, 1, 0) };
        sidebarSurface.SetResourceReference(Border.BackgroundProperty, "SidebarBrush");
        sidebarSurface.SetResourceReference(Border.BorderBrushProperty, "DividerBrush");
        layout.Children.Add(sidebarSurface);
        var brand = new StackPanel { Margin = new Thickness(9, 0, 0, 27) };
        var brandMark = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(10), HorizontalAlignment = HorizontalAlignment.Left, Child = UiIcon.Create(AppIcon.App, 22, "AccentTextBrush"), Padding = new Thickness(6) };
        brandMark.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        brand.Children.Add(brandMark);
        brand.Children.Add(T("PHP DevTray", 15, true, margin: new Thickness(0, 11, 0, 1)));
        brand.Children.Add(T("PHP DEVELOPMENT", 10, muted: true));
        DockPanel.SetDock(brand, Dock.Top); sidebar.Children.Add(brand);
        var lower = new StackPanel();
        lower.Children.Add(Nav("Settings", "Settings", AppIcon.Settings));
        lower.Children.Add(T("Local development", 10, muted: true, margin: new Thickness(12, 16, 0, 0)));
        DockPanel.SetDock(lower, Dock.Bottom); sidebar.Children.Add(lower);
        var nav = new StackPanel();
        nav.Children.Add(T("WORKSPACE", 10, muted: true, margin: new Thickness(12, 0, 0, 9)));
        nav.Children.Add(Nav("PHP Versions", "PHP runtimes", AppIcon.Terminal));
        nav.Children.Add(Nav("Nginx Sites", "Nginx", AppIcon.Globe));
        nav.Children.Add(Nav("Mail Trap", "Mail Trap", AppIcon.Mail));
        nav.Children.Add(Nav("Logs", "Activity", AppIcon.Logs));
        var navDivider = new Border { Height = 1, Margin = new Thickness(12, 17, 12, 15) }; navDivider.SetResourceReference(Border.BackgroundProperty, "DividerBrush"); nav.Children.Add(navDivider);
        nav.Children.Add(Nav("Updates", "Install runtimes", AppIcon.Download));
        sidebar.Children.Add(nav);

        var main = new Grid(); Grid.SetColumn(main, 1); layout.Children.Add(main);
        main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(78) });
        main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(92) });
        main.RowDefinitions.Add(new RowDefinition());
        main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
        var header = new Grid { Margin = new Thickness(24, 0, 17, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var identity = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        identity.Children.Add(T("Your local environment", 18, true));
        _overall = T(string.Empty, 11, muted: true, margin: new Thickness(0, 4, 0, 0)); identity.Children.Add(_overall); header.Children.Add(identity);
        var headerActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _globalAction = new Button { MinWidth = 104, Margin = new Thickness(0, 0, 9, 0) };
        _globalAction.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        _globalAction.Click += async (_, _) => await RunAsync(AnyRunning ? "Stopping services…" : "Starting services…", () => AnyRunning ? _manager.StopAllAsync(_lifetime.Token) : _manager.StartAllAsync(_lifetime.Token));
        headerActions.Children.Add(_globalAction);

        Grid.SetColumn(headerActions, 1); header.Children.Add(headerActions); main.Children.Add(header);

        var serviceStrip = new UniformGrid { Columns = 3, Margin = new Thickness(24, 8, 18, 8) };
        AddServiceChip(serviceStrip, ServiceKind.Nginx, "Nginx", AppIcon.Globe, "Nginx Sites");
        AddServiceChip(serviceStrip, ServiceKind.Php, "PHP", AppIcon.Terminal, "PHP Versions");
        AddServiceChip(serviceStrip, ServiceKind.Mailpit, "Mail Trap", AppIcon.Mail, "Mail Trap");
        var serviceSurface = new Border { Child = serviceStrip, BorderThickness = new Thickness(0, 1, 0, 1) };
        serviceSurface.SetResourceReference(Border.BorderBrushProperty, "DividerBrush"); Grid.SetRow(serviceSurface, 1); main.Children.Add(serviceSurface);

        var page = new Grid { Margin = new Thickness(24, 22, 24, 0) };
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); page.RowDefinitions.Add(new RowDefinition());
        var errorLayout = new DockPanel();
        var dismiss = new Button { Content = UiIcon.Create(AppIcon.Close, 15), ToolTip = "Dismiss error", VerticalAlignment = VerticalAlignment.Top };
        dismiss.SetResourceReference(StyleProperty, "IconButtonStyle"); dismiss.Click += (_, _) => { _dismissedError = _error; _error = null; Render(_snapshot); }; AutomationProperties.SetName(dismiss, "Dismiss error"); DockPanel.SetDock(dismiss, Dock.Right); errorLayout.Children.Add(dismiss);
        _errorText = T(string.Empty, 12); _errorText.VerticalAlignment = VerticalAlignment.Center; _errorText.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush"); AutomationProperties.SetLiveSetting(_errorText, AutomationLiveSetting.Polite);
        var errorMessage = new ScrollViewer { Content = _errorText, MaxHeight = 96, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(errorMessage, "Notification details"); errorLayout.Children.Add(errorMessage);
        var errorContent = new StackPanel(); errorContent.Children.Add(errorLayout);
        var errorActions = new WrapPanel();
        var copyError = new Button { Content = "Copy details", Margin = new Thickness(0, 4, 6, 0) }; copyError.SetResourceReference(StyleProperty, "QuietButtonStyle");
        AutomationProperties.SetName(copyError, "Copy error details");
        copyError.Click += (_, _) => { try { _ = CopyTextAsync(_errorText.Text, "Error details copied to clipboard."); } catch (Exception exception) { ShowError(exception.Message); } };
        errorActions.Children.Add(copyError);
        var showActivity = new Button { Content = "View activity", Margin = new Thickness(0, 4, 0, 0) }; showActivity.SetResourceReference(StyleProperty, "QuietButtonStyle");
        AutomationProperties.SetName(showActivity, "View activity to investigate this error"); showActivity.Click += async (_, _) => await NavigateAsync("Logs"); errorActions.Children.Add(showActivity);
        errorContent.Children.Add(errorActions);
        _errorBanner = Surface(errorContent, "CardBrush", 8, new Thickness(10, 4, 4, 4)); _errorBanner.Margin = new Thickness(0, 0, 0, 12); _errorBanner.Visibility = Visibility.Collapsed; page.Children.Add(_errorBanner);
        var heading = new Grid { Margin = new Thickness(0, 0, 0, 18) }; heading.ColumnDefinitions.Add(new ColumnDefinition()); heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleBlock = new StackPanel(); _pageTitle = T(string.Empty, 22, true); _pageDescription = T(string.Empty, 12, muted: true, margin: new Thickness(0, 5, 16, 0)); titleBlock.Children.Add(_pageTitle); titleBlock.Children.Add(_pageDescription); heading.Children.Add(titleBlock);
        _pageActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top }; Grid.SetColumn(_pageActions, 1); heading.Children.Add(_pageActions); Grid.SetRow(heading, 1); page.Children.Add(heading);
        _contentHost = new Grid { ClipToBounds = true }; Grid.SetRow(_contentHost, 2); page.Children.Add(_contentHost); Grid.SetRow(page, 2); main.Children.Add(page);

        var footer = new Grid { Margin = new Thickness(24, 0, 18, 0) }; footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _operationText = T(string.Empty, 10, muted: true); _operationText.VerticalAlignment = VerticalAlignment.Center; AutomationProperties.SetLiveSetting(_operationText, AutomationLiveSetting.Polite); footer.Children.Add(_operationText);
        var refresh = _environmentRefresh = new Button { Content = UiIcon.Create(AppIcon.Refresh, 15), ToolTip = "Refresh environment" }; refresh.SetResourceReference(StyleProperty, "IconButtonStyle"); AutomationProperties.SetName(refresh, "Refresh environment"); refresh.Click += async (_, _) => await RunAsync("Refreshing…", () => _manager.RefreshAsync(_lifetime.Token)); Grid.SetColumn(refresh, 1); footer.Children.Add(refresh); Grid.SetRow(footer, 3); main.Children.Add(footer);

        SizeChanged += (_, _) =>
        {
            var compact = ActualWidth < 730;
            foreach (var glyph in _serviceGlyphs) glyph.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            layout.ColumnDefinitions[0].Width = new GridLength(compact ? 64 : 184);
            brand.Margin = compact ? new Thickness(3, 0, 0, 27) : new Thickness(9, 0, 0, 27);
            foreach (var text in brand.Children.OfType<TextBlock>()) text.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            foreach (var text in nav.Children.OfType<TextBlock>().Concat(lower.Children.OfType<TextBlock>())) text.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            foreach (var button in _navigation.Values)
            {
                button.HorizontalContentAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Left;
                if (button.Content is StackPanel items)
                {
                    items.Children[0].SetValue(MarginProperty, compact ? new Thickness(0) : new Thickness(0, 0, 8, 0));
                    items.Children[1].Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
                }
            }
        };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) Hide(); else _restoreState = WindowState; };
        _pageRefresher = new PageAutoRefresher(Dispatcher, RefreshVisiblePageAsync);
        IsVisibleChanged += (_, _) => UpdatePageRefresh();
        Closing += OnClosing;
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && _mailSearch is not null) { _mailSearch.Focus(); _mailSearch.SelectAll(); args.Handled = true; }

        };
        Render(_snapshot);
    }

    public void RestoreWindow()
    {
        if (_shutdown) return;
        Render(_manager.Snapshot);
        if (WindowState == WindowState.Minimized) WindowState = _restoreState;
        Show(); Activate();
    }

    internal async Task VerifyWindowLifecycleForSmokeAsync()
    {
        if (!ShowInTaskbar || Topmost || AllowsTransparency || ResizeMode != ResizeMode.CanResize || WindowStyle != WindowStyle.SingleBorderWindow)
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
        _section = section; _renderedSection = null; Render(_snapshot);
    }

    public void Render(EnvironmentSnapshot snapshot)
    {
        _snapshot = snapshot;
        if (snapshot.LastError is { Length: > 0 } remote && remote != _dismissedError) _error ??= remote;
        var runningCount = snapshot.Services.Count(s => s.Status == ServiceStatus.Running);
        _overall.Text = snapshot.IsBusy ? "Applying changes…" : snapshot.Services.Any(s => s.Status == ServiceStatus.Failed) ? "One or more services need attention" : AnyRunning ? $"{runningCount} {(runningCount == 1 ? "service" : "services")} running" : "All services stopped";
        _globalAction.Content = IconLabel(AnyRunning ? AppIcon.Stop : AppIcon.Play, AnyRunning ? "Stop all" : "Start all");
        AutomationProperties.SetName(_globalAction, AnyRunning ? "Stop all services" : "Start all services");
        _globalAction.IsEnabled = !Busy && snapshot.Services.Any(s => s.Status != ServiceStatus.NotInstalled);
        _operationText.Text = _shutdown ? "Stopping services before exit…" : _operation ?? (snapshot.IsBusy ? "Working…" : _feedbackMessage ?? "Services continue in tray · Exit from tray menu to stop");
        _environmentRefresh.Visibility = _section is "Mail Trap" or "Logs" ? Visibility.Collapsed : Visibility.Visible;
        _errorText.Text = _error; _errorBanner.Visibility = string.IsNullOrWhiteSpace(_error) ? Visibility.Collapsed : Visibility.Visible;
        foreach (var (key, button) in _navigation) button.Tag = key == _section ? "Selected" : null;
        UpdateServiceStrip();
        var signature = StructureSignature();
        if (_renderedSection != _section || _bodySignature != signature)
        {
            _renderedSection = _section; _bodySignature = signature; _bodyButtons.Clear(); _contentHost.Children.Clear(); _pageActions.Children.Clear(); _phpRows.Clear(); _inlineServices.Clear(); _siteVersionPickers.Clear();
            _mailList = null; _mailSearch = null; _mailPreview = null; _mailResults = null; _mailNoResults = null; _mailDetail = null; _logOutput = null; _releaseContent = null; _releaseVersionPicker = null; _pathSetting = null; _defaultPhpPicker = null; _themeButtons.Clear();
            switch (_section)
            {
                case "PHP Versions": BuildPhp(); break;
                case "Nginx Sites": BuildSites(); break;
                case "Mail Trap": BuildMail(); break;
                case "Logs": BuildLogs(); break;
                case "Updates": BuildInstall(); break;
                case "Settings": BuildSettings(); break;
            }
        }
        foreach (var row in _phpRows.Values) UpdatePhpRow(row);
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
        foreach (var picker in _siteVersionPickers) picker.IsEnabled = !Busy;
        UpdatePageRefresh();
    }

    private string StructureSignature() => _section switch
    {
        "PHP Versions" => string.Join('|', _snapshot.PhpInstallations.Select(p => $"{p.Id}:{p.Version}:{p.Port}")) + _snapshot.DefaultPhpChannel + string.Join('|', _snapshot.Sites.Select(s => s.PhpChannel)) + string.Join('|', _snapshot.RetainedPhpVersions.Select(p => $"{p.Channel}:{p.Version}")),
        "Nginx Sites" => string.Join('|', _snapshot.Sites.Select(s => $"{s.Id}:{s.Hostname}:{s.DocumentRoot}:{s.PhpChannel}:{s.Enabled}:{s.Port}")) + string.Join('|', _snapshot.PhpInstallations.Select(p => p.Channel)) + HasRuntime(ServiceKind.Nginx),
        "Mail Trap" => HasRuntime(ServiceKind.Mailpit) + ":" + MailIsRunning + ":" + (_messages.Count > 0),
        "Settings" => "Settings:" + string.Join('|', _snapshot.PhpInstallations.Select(p => p.Channel)),
        _ => _section,
    };

    private Button Nav(string key, string label, AppIcon icon)
    {
        var button = new Button { Content = IconLabel(icon, label), ToolTip = label, Margin = new Thickness(0, 0, 0, 4), HorizontalContentAlignment = HorizontalAlignment.Left };
        button.SetResourceReference(StyleProperty, "NavigationButtonStyle"); AutomationProperties.SetName(button, label);
        button.Click += async (_, _) => await NavigateAsync(key); _navigation[key] = button; return button;
    }

    private void AddServiceChip(Panel parent, ServiceKind kind, string label, AppIcon icon, string section)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(T(label, 13, true));
        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
        var dot = new Border { Width = 5, Height = 5, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) };
        statusRow.Children.Add(dot); _serviceDots[kind] = dot;
        var status = T(string.Empty, 11, muted: true); statusRow.Children.Add(status); text.Children.Add(statusRow); _serviceLabels[kind] = status;
        var button = new Button { Content = text, Padding = new Thickness(12, 8, 50, 8), HorizontalContentAlignment = HorizontalAlignment.Left, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        button.SetResourceReference(StyleProperty, "QuietButtonStyle");
        AutomationProperties.SetName(button, $"Open {label}"); button.Click += async (_, _) => await NavigateAsync(section); _serviceButtons[kind] = button;
        var chip = new Grid(); chip.Children.Add(button);
        var toggle = new Button { Content = UiIcon.Create(AppIcon.Play, 15), Width = 40, Height = 40, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        toggle.SetResourceReference(StyleProperty, "SecondaryButtonStyle");
        toggle.Click += async (_, _) => { if (kind == ServiceKind.Php) await ToggleAllPhpAsync(); else await ToggleServiceAsync(kind); };
        _serviceToggles[kind] = toggle; chip.Children.Add(toggle);
        var card = Surface(chip, "CardBrush", 10, new Thickness(0)); card.Margin = new Thickness(0, 0, 8, 0); _serviceCards[kind] = card; parent.Children.Add(card);
    }

    private void UpdateServiceStrip()
    {
        foreach (var kind in _serviceLabels.Keys)
        {
            var services = _snapshot.Services.Where(s => s.Kind == kind && s.Status != ServiceStatus.NotInstalled).ToArray();
            var state = services.Length == 0 ? ServiceStatus.NotInstalled : services.Any(s => s.Status == ServiceStatus.Failed) ? ServiceStatus.Failed : services.Any(s => IsTransition(s.Status)) ? ServiceStatus.Starting : services.Any(s => s.Status == ServiceStatus.Running) ? ServiceStatus.Running : ServiceStatus.Stopped;
            var active = services.Count(s => s.Status == ServiceStatus.Running);
            _serviceLabels[kind].Text = kind == ServiceKind.Php && services.Length > 0 && state == ServiceStatus.Running
                ? active == services.Length ? $"{active} running" : $"{active}/{services.Length} running"
                : kind == ServiceKind.Php && state == ServiceStatus.Stopped ? $"{services.Length} stopped" : StatusLabel(state);
            var name = kind == ServiceKind.Php ? "PHP" : kind == ServiceKind.Nginx ? "Nginx" : "Mail Trap";
            AutomationProperties.SetName(_serviceButtons[kind], $"{name}, {_serviceLabels[kind].Text}. Open details");
            _serviceDots[kind].SetResourceReference(Border.BackgroundProperty, StatusBrush(state));
            var selected = _section == (kind == ServiceKind.Php ? "PHP Versions" : kind == ServiceKind.Nginx ? "Nginx Sites" : "Mail Trap");
            _serviceCards[kind].SetResourceReference(Border.BackgroundProperty, selected ? "AccentSoftBrush" : "CardBrush");
            _serviceCards[kind].SetResourceReference(Border.BorderBrushProperty, selected ? "AccentBrush" : "BorderBrush");
            var toggle = _serviceToggles[kind]; var running = services.Any(service => service.Status == ServiceStatus.Running);
            toggle.Content = UiIcon.Create(running ? AppIcon.Stop : AppIcon.Play, 15);
            toggle.ToolTip = $"{(running ? "Stop" : "Start")} {(kind == ServiceKind.Php ? "all PHP runtimes" : name)}";
            AutomationProperties.SetName(toggle, toggle.ToolTip.ToString());
            toggle.IsEnabled = services.Length > 0 && !Busy;
        }
    }

    private Task ToggleAllPhpAsync() => RunAsync("Updating PHP runtimes…", async () =>
    {
        var runtimes = _snapshot.Services.Where(service => service.Kind == ServiceKind.Php && service.Status != ServiceStatus.NotInstalled).ToArray();
        if (runtimes.Any(service => service.Status == ServiceStatus.Running))
        {
            if (_snapshot.Services.Any(service => service.Kind == ServiceKind.Nginx && service.Status == ServiceStatus.Running) &&
                _snapshot.Sites.Any(site => site.Enabled && runtimes.Any(runtime => runtime.Id == $"php:{site.PhpChannel}")))
                await _manager.StopServiceAsync("nginx", _lifetime.Token);
            foreach (var runtime in runtimes) await _manager.StopServiceAsync(runtime.Id, _lifetime.Token);
        }
        else foreach (var runtime in runtimes) await _manager.StartServiceAsync(runtime.Id, _lifetime.Token);
    });

    private void BuildPhp()
    {
        Heading("PHP runtimes", "Manage versions, extensions and command-line PHP.");
        _pageActions.Children.Add(ActionButton("Install PHP", () => OpenInstallAsync(ServiceKind.Php), "PrimaryButtonStyle", AppIcon.Plus));
        _pageActions.Children.Add(ActionButton("Open config folder", () => OpenPathAsync(Path.Combine(_snapshot.DataDirectory, "user", "php")), "SecondaryButtonStyle", AppIcon.Folder, "Open PHP configuration folder", independent: true));
        if (_snapshot.PhpInstallations.Count == 0)
        {
            _contentHost.Children.Add(Empty(AppIcon.Terminal, "Start with PHP", "Install a PHP runtime, then give each project the version it needs.", "Install PHP", () => OpenInstallAsync(ServiceKind.Php), "Import an existing runtime", () => BrowseRuntimeAsync(ServiceKind.Php), "01  Install a runtime     02  Add a site     03  Start building"));
            return;
        }
        var rows = new StackPanel();
        foreach (var installation in _snapshot.PhpInstallations.OrderByDescending(p => Version.TryParse(p.Version, out var version) ? version : new Version()))
        {
            var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) }); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var mark = IconTile(AppIcon.Terminal, 34); mark.VerticalAlignment = VerticalAlignment.Center; grid.Children.Add(mark);
            var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0) };
            var title = new WrapPanel(); title.Children.Add(T($"PHP {installation.Channel}", 14, true));
            if (_snapshot.DefaultPhpChannel == installation.Channel) title.Children.Add(Badge("CLI default", "AccentSoftBrush", new Thickness(8, 0, 0, 0)));
            var siteCount = _snapshot.Sites.Count(s => s.PhpChannel == installation.Channel);
            labels.Children.Add(title); labels.Children.Add(T($"{installation.Version}  ·  127.0.0.1:{installation.Port}  ·  {siteCount} {(siteCount == 1 ? "site" : "sites")}", 11, muted: true, margin: new Thickness(0, 5, 0, 0)));
            var status = T(string.Empty, 11, muted: true, margin: new Thickness(0, 5, 0, 0)); labels.Children.Add(status); Grid.SetColumn(labels, 1); grid.Children.Add(labels);
            var retained = _snapshot.RetainedPhpVersions.Count(version => version.Channel == installation.Channel);
            if (retained > 0) labels.Children.Add(T($"{retained} previous {(retained == 1 ? "patch" : "patches")} available in actions", 11, muted: true, margin: new Thickness(0, 5, 0, 0)));
            var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var toggle = ActionButton("Start", () => TogglePhpAsync(installation), "SecondaryButtonStyle", AppIcon.Play, $"Start PHP {installation.Channel}"); controls.Children.Add(toggle);
            controls.Children.Add(MoreButton($"PHP {installation.Channel} actions", () => PhpMenu(installation))); Grid.SetColumn(controls, 2); grid.Children.Add(controls);
            rows.Children.Add(RowSurface(grid)); _phpRows[installation.Id] = new PhpRow(installation, status, toggle);
        }
        rows.Children.Add(T($"CLI default: PHP {_snapshot.DefaultPhpChannel ?? "not selected"}", 11, muted: true, margin: new Thickness(1, 5, 0, 0)));
        _contentHost.Children.Add(Scroll(rows));
    }

    private void UpdatePhpRow(PhpRow row)
    {
        var service = PhpService(row.Installation); var running = service?.Status == ServiceStatus.Running;
        row.Status.Text = service?.Status == ServiceStatus.Failed ? service.Detail ?? "Failed to start" : StatusLabel(service?.Status ?? ServiceStatus.Stopped);
        row.Status.SetResourceReference(TextBlock.ForegroundProperty, service?.Status == ServiceStatus.Running ? "SuccessBrush" : service?.Status == ServiceStatus.Failed ? "ErrorBrush" : "MutedBrush");
        row.Toggle.Content = IconLabel(running ? AppIcon.Stop : AppIcon.Play, running ? "Stop" : "Start", 14);
        AutomationProperties.SetName(row.Toggle, $"{(running ? "Stop" : "Start")} PHP {row.Installation.Channel}");
    }

    private Task TogglePhpAsync(PhpInstallation installation) => RunAsync($"Updating PHP {installation.Channel}…", async () =>
    {
        var service = PhpService(installation) ?? throw new InvalidOperationException("Refresh the runtime list and try again.");
        if (service.Status == ServiceStatus.Running) await _manager.StopServiceAsync(service.Id, _lifetime.Token); else await _manager.StartServiceAsync(service.Id, _lifetime.Token);
    });

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
                option.ToolTip = "Keep this channel's sites, port, extensions, and CLI selection. A running channel and dependent Nginx briefly restart.";
                versions.Items.Add(option);
            }
            yield return versions;
        }
        yield return Menu("Manage extensions…", () => ShowExtensionsAsync(installation.Channel), !Busy);
        yield return Menu("Edit PHP configuration", () => OpenEditorAsync(Path.Combine(_snapshot.DataDirectory, "user", "php", installation.Channel, "conf.d", "20-custom.ini")));
        yield return Menu("Open runtime folder", () => OpenPathAsync(installation.Directory));
    }

    private void BuildSites()
    {
        Heading("Nginx", "Manage local sites and their configuration.");
        var create = ActionButton("Create site", ShowCreateSiteAsync, "PrimaryButtonStyle", AppIcon.Plus, enabled: () => !Busy && _snapshot.PhpInstallations.Count > 0); _pageActions.Children.Add(create);
        _pageActions.Children.Add(ActionButton("Open config folder", () => OpenPathAsync(Path.Combine(_snapshot.DataDirectory, "user", "nginx")), "SecondaryButtonStyle", AppIcon.Folder, "Open Nginx configuration folder", independent: true));
        if (_snapshot.Sites.Count == 0)
        {
            var hasPhp = _snapshot.PhpInstallations.Count > 0;
            _contentHost.Children.Add(Empty(AppIcon.Sites, "Your next project starts here", "Connect a document root, choose PHP, and give your project a local address.", hasPhp ? "Create site" : "Install PHP first", hasPhp ? ShowCreateSiteAsync : () => OpenInstallAsync(ServiceKind.Php), HasRuntime(ServiceKind.Nginx) ? "Open Nginx configuration" : "Install Nginx", HasRuntime(ServiceKind.Nginx) ? () => OpenPathAsync(Path.Combine(_snapshot.DataDirectory, "user", "nginx")) : () => OpenInstallAsync(ServiceKind.Nginx)));
            return;
        }
        var rows = new StackPanel();
        if (!HasRuntime(ServiceKind.Nginx)) rows.Children.Add(InlineNotice("Install Nginx to serve these sites.", "Install Nginx", () => OpenInstallAsync(ServiceKind.Nginx)));
        foreach (var site in _snapshot.Sites)
        {
            var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var info = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
            var title = new WrapPanel { VerticalAlignment = VerticalAlignment.Center }; var hostname = T(site.Hostname, 14, true); hostname.VerticalAlignment = VerticalAlignment.Center; hostname.Margin = new Thickness(0, 0, 12, 0); title.Children.Add(hostname);
            var picker = new ComboBox { ItemsSource = _snapshot.PhpInstallations.Select(p => p.Channel).Append(site.PhpChannel).Distinct().Select(channel => new { Channel = channel, Label = "PHP " + channel }).ToArray(), DisplayMemberPath = "Label", SelectedValuePath = "Channel", SelectedValue = site.PhpChannel, MinWidth = 100, MinHeight = 36, FontSize = 12 };
            AutomationProperties.SetName(picker, $"PHP version for {site.Hostname}");
            var changing = false;
            picker.SelectionChanged += async (_, _) =>
            {
                if (changing || picker.SelectedValue is not string channel || channel == site.PhpChannel || Busy) return;
                changing = true;
                try { await RunAsync($"Switching {site.Hostname} to PHP {channel}…", () => _manager.SaveSiteAsync(site with { PhpChannel = channel }, _lifetime.Token)); }
                finally { picker.SelectedValue = _snapshot.Sites.FirstOrDefault(current => current.Id == site.Id)?.PhpChannel ?? site.PhpChannel; changing = false; }
            };
            picker.IsEnabled = !Busy;
            _siteVersionPickers.Add(picker);
            title.Children.Add(picker); info.Children.Add(title);
            var enabled = Badge(site.Enabled ? "✓ Enabled" : "○ Disabled", "HoverBrush", new Thickness(0, 8, 0, 0)); enabled.HorizontalAlignment = HorizontalAlignment.Left; if (enabled.Child is TextBlock enabledText) { enabledText.FontSize = 12; enabledText.SetResourceReference(TextBlock.ForegroundProperty, site.Enabled ? "SuccessBrush" : "MutedBrush"); } info.Children.Add(enabled);
            info.Children.Add(T(site.DocumentRoot, 11, muted: true, margin: new Thickness(0, 7, 0, 0)));
            info.Children.Add(T($"Port {site.Port}", 11, muted: true, margin: new Thickness(0, 5, 0, 0))); grid.Children.Add(info);
            var actions = MoreButton($"{site.Hostname} actions", () => SiteMenu(site)); actions.VerticalAlignment = VerticalAlignment.Top; Grid.SetColumn(actions, 1); grid.Children.Add(actions); rows.Children.Add(RowSurface(grid));
        }
        rows.Children.Add(T("Custom hostnames must resolve to 127.0.0.1 on this PC.", 11, muted: true, margin: new Thickness(1, 6, 0, 0)));
        _contentHost.Children.Add(Scroll(rows));
    }

    private IEnumerable<MenuItem> SiteMenu(SiteDefinition site)
    {
        yield return Menu(site.Enabled ? "Disable site" : "Enable site", () => RunAsync("Updating site…", () => _manager.SetSiteEnabledAsync(site.Id, !site.Enabled, _lifetime.Token)), !Busy);
        yield return Menu("Edit config", () => OpenEditorAsync(Path.Combine(_snapshot.DataDirectory, "user", "nginx", "sites-available", site.Id + ".conf")));
        yield return Menu("Open site in browser", () => OpenPathAsync($"http://{site.Hostname}:{site.Port}"));
        yield return Menu("Open document root", () => OpenPathAsync(site.DocumentRoot));
        yield return Menu("Copy hosts entry", () => CopyTextAsync($"127.0.0.1 {site.Hostname}", "Hosts entry copied to clipboard."));
    }

    private IEnumerable<MenuItem> NginxMenu()
    {
        if (_snapshot.Services.FirstOrDefault(s => s.Kind == ServiceKind.Nginx && s.Status != ServiceStatus.NotInstalled) is { } nginx)
        {
            yield return Menu(nginx.Status == ServiceStatus.Running ? "Stop Nginx" : "Start Nginx", () => RunAsync("Updating Nginx…", () => nginx.Status == ServiceStatus.Running ? _manager.StopServiceAsync(nginx.Id, _lifetime.Token) : _manager.StartServiceAsync(nginx.Id, _lifetime.Token)), !Busy);
            yield return Menu("Restart Nginx", () => RunAsync("Restarting Nginx…", () => _manager.RestartServiceAsync(nginx.Id, _lifetime.Token)), !Busy);
        }
        else yield return Menu("Install Nginx…", () => OpenInstallAsync(ServiceKind.Nginx), !Busy);
        yield return Menu("Import Nginx…", () => BrowseRuntimeAsync(ServiceKind.Nginx), !Busy);
        yield return Menu("Open configuration folder", () => OpenPathAsync(Path.Combine(_snapshot.DataDirectory, "user", "nginx")));
    }

    private void BuildMail()
    {
        Heading("Mail Trap", "A safe inbox for development mail.");
        if (MailIsRunning) _pageActions.Children.Add(ActionButton("", CopySmtpAsync, "IconButtonStyle", AppIcon.Copy, "Copy SMTP settings", independent: true));
        if (!HasRuntime(ServiceKind.Mailpit))
        {
            _contentHost.Children.Add(Empty(AppIcon.Mail, "Keep test email close", "Capture messages from your apps in a local inbox, without sending them to real recipients.", "Install Mailpit", () => OpenInstallAsync(ServiceKind.Mailpit), "Import Mailpit", () => BrowseRuntimeAsync(ServiceKind.Mailpit)));
            return;
        }
        var outer = new Grid(); outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); outer.RowDefinitions.Add(new RowDefinition());
        outer.Children.Add(ServiceControl(ServiceKind.Mailpit, $"SMTP 127.0.0.1:{_snapshot.SmtpPort}"));
        if (!MailIsRunning)
        {
            var stopped = Empty(AppIcon.Mail, "Mail Trap is stopped", "Start Mail Trap to capture and read development email. Previously captured messages stay stored.", "Start Mail Trap", StartMailAsync, "Copy SMTP settings", CopySmtpAsync, primaryIcon: AppIcon.Play);
            Grid.SetRow(stopped, 1); outer.Children.Add(stopped);
        }
        else if (_messages.Count == 0)
        {
            var empty = Empty(AppIcon.Mail, "Your inbox is clear", $"Send a test message to 127.0.0.1:{_snapshot.SmtpPort}. New messages appear automatically.", "Copy SMTP settings", CopySmtpAsync, primaryIcon: AppIcon.Copy); Grid.SetRow(empty, 1); outer.Children.Add(empty);
        }
        else
        {
            var inbox = new Grid { Margin = new Thickness(0, 1, 0, 0) }; inbox.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) }); inbox.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) }); inbox.ColumnDefinitions.Add(new ColumnDefinition());
            var listArea = new Grid { Margin = new Thickness(0, 0, 13, 0) }; listArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); listArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); listArea.RowDefinitions.Add(new RowDefinition());
            var searchInput = new Grid { Margin = new Thickness(0, 0, 0, 9) };
            _mailSearch = new TextBox { ToolTip = "Search subject or sender", Padding = new Thickness(33, 8, 10, 8), MinHeight = 40 };
            AutomationProperties.SetName(_mailSearch, "Search captured messages"); searchInput.Children.Add(_mailSearch);
            AutomationProperties.SetHelpText(_mailSearch, "Search subject or sender. Press Control F to focus search.");
            var searchIcon = UiIcon.Create(AppIcon.Search, 15, "MutedBrush"); searchIcon.HorizontalAlignment = HorizontalAlignment.Left; searchIcon.VerticalAlignment = VerticalAlignment.Center; searchIcon.Margin = new Thickness(11, 0, 0, 0); searchIcon.IsHitTestVisible = false; searchInput.Children.Add(searchIcon);
            var placeholder = T("Search mail", 12, muted: true, margin: new Thickness(33, 0, 0, 0)); placeholder.VerticalAlignment = VerticalAlignment.Center; placeholder.IsHitTestVisible = false; searchInput.Children.Add(placeholder);
            _mailSearch.TextChanged += (_, _) => { placeholder.Visibility = _mailSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; FillMailList(); };
            listArea.Children.Add(searchInput);
            _mailResults = T(string.Empty, 11, muted: true, margin: new Thickness(0, 0, 0, 7));
            AutomationProperties.SetLiveSetting(_mailResults, AutomationLiveSetting.Polite);
            Grid.SetRow(_mailResults, 1); listArea.Children.Add(_mailResults);
            _mailList = new ListBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            ScrollViewer.SetHorizontalScrollBarVisibility(_mailList, ScrollBarVisibility.Disabled);
            AutomationProperties.SetName(_mailList, "Captured messages"); _mailList.SelectionChanged += async (_, _) =>
            {
                if (_updatingInputs || _mailList.SelectedItem is not ListBoxItem { Tag: MailMessageSummary selected }) return;
                await ReadMessageAsync(selected);
            };
            Grid.SetRow(_mailList, 2); listArea.Children.Add(_mailList);
            var noResults = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
            noResults.Children.Add(T("No matching messages", 13, true));
            noResults.Children.Add(T("Try another subject or sender, or clear your search.", 12, muted: true, margin: new Thickness(0, 6, 0, 12)));
            var clearSearch = ActionButton("Clear search", () => { if (_mailSearch is not null) { _mailSearch.Clear(); _mailSearch.Focus(); } return Task.CompletedTask; }, "SecondaryButtonStyle", independent: true);
            clearSearch.HorizontalAlignment = HorizontalAlignment.Left; noResults.Children.Add(clearSearch);
            _mailNoResults = new Border { Child = noResults, Padding = new Thickness(6, 12, 6, 0), Visibility = Visibility.Collapsed };
            Grid.SetRow(_mailNoResults, 2); listArea.Children.Add(_mailNoResults); inbox.Children.Add(listArea);
            var separator = new Border(); separator.SetResourceReference(Border.BackgroundProperty, "DividerBrush"); Grid.SetColumn(separator, 1); inbox.Children.Add(separator);
            var detail = new Grid { Margin = new Thickness(17, 0, 0, 0) }; detail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); detail.RowDefinitions.Add(new RowDefinition());
            _mailDetail = detail;
            var messageHeader = new StackPanel { Margin = new Thickness(0, 0, 0, 13) }; _mailSubject = T("Select a message", 16, true); _mailMetadata = T(string.Empty, 11, muted: true, margin: new Thickness(0, 7, 0, 0)); messageHeader.Children.Add(_mailSubject); messageHeader.Children.Add(_mailMetadata); detail.Children.Add(messageHeader);
            _mailPreview = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0), Padding = new Thickness(0), Background = Brushes.Transparent, Text = _mailText };
            AutomationProperties.SetName(_mailPreview, "Message plain-text content"); Grid.SetRow(_mailPreview, 1); detail.Children.Add(_mailPreview); Grid.SetColumn(detail, 2); inbox.Children.Add(detail); Grid.SetRow(inbox, 1); outer.Children.Add(inbox);
            FillMailList();
        }
        _contentHost.Children.Add(outer);
    }

    private void FillMailList()
    {
        if (_mailList is null) return;
        var scroller = FindScrollViewer(_mailList);
        var offset = scroller?.VerticalOffset ?? 0;
        _updatingInputs = true;
        _mailList.Items.Clear(); var query = _mailSearch?.Text.Trim() ?? string.Empty;
        foreach (var message in _messages.Where(m => m.Subject.Contains(query, StringComparison.OrdinalIgnoreCase) || m.From.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            var content = new StackPanel { Margin = new Thickness(7, 9, 7, 9) };
            var subject = T(message.Subject, 12, true); subject.TextWrapping = TextWrapping.NoWrap; subject.TextTrimming = TextTrimming.CharacterEllipsis; subject.ToolTip = message.Subject; content.Children.Add(subject);
            var sender = T(message.From, 11, muted: true, margin: new Thickness(0, 4, 0, 0)); sender.TextWrapping = TextWrapping.NoWrap; sender.TextTrimming = TextTrimming.CharacterEllipsis; sender.ToolTip = message.From; content.Children.Add(sender);
            content.Children.Add(T(message.Created.LocalDateTime.ToString("g"), 11, muted: true, margin: new Thickness(0, 4, 0, 0)));
            var item = new ListBoxItem { Content = content, Tag = message, HorizontalContentAlignment = HorizontalAlignment.Stretch }; AutomationProperties.SetName(item, $"{message.Subject}, from {message.From}"); _mailList.Items.Add(item);
            if (message.Id == _selectedMessageId) _mailList.SelectedItem = item;
        }
        _updatingInputs = false;
        if (_mailResults is not null) _mailResults.Text = query.Length == 0 ? $"{_messages.Count} {(_messages.Count == 1 ? "message" : "messages")}" : $"{_mailList.Items.Count} of {_messages.Count} messages";
        var noMatches = _mailList.Items.Count == 0;
        if (_mailNoResults is not null) _mailNoResults.Visibility = noMatches ? Visibility.Visible : Visibility.Collapsed;
        if (_mailDetail is not null) _mailDetail.Visibility = noMatches ? Visibility.Collapsed : Visibility.Visible;
        _mailList.Visibility = noMatches ? Visibility.Collapsed : Visibility.Visible;
        _renderedMessages = _messages;
        scroller?.ScrollToVerticalOffset(offset);
        if (_selectedMessageId is { } id && _messages.FirstOrDefault(m => m.Id == id) is { } selected) SetMessageHeader(selected);
    }

    private async Task ReadMessageAsync(MailMessageSummary message, CancellationToken refreshToken = default)
    {
        _selectedMessageId = message.Id; SetMessageHeader(message);
        if (_loadedMessageId != message.Id) { _mailText = string.Empty; _loadedMessageId = null; }
        _messageCancellation?.Cancel(); _messageCancellation?.Dispose();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, refreshToken); _messageCancellation = cancellation;
        if (_mailPreview is not null) _mailPreview.Text = "Loading message…";
        try
        {
            var text = await _manager.GetMessageTextAsync(message.Id, cancellation.Token);
            if (!cancellation.IsCancellationRequested && _selectedMessageId == message.Id) { _mailText = text; _loadedMessageId = message.Id; if (_mailPreview is not null) _mailPreview.Text = text; }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (_selectedMessageId == message.Id && _mailPreview is not null) _mailPreview.Text = "This message could not be loaded. Select another message, then select this message to try again. Check Activity if the problem continues.";
            if (refreshToken.CanBeCanceled) throw;
            ShowError(exception.Message);
        }
        finally { if (ReferenceEquals(_messageCancellation, cancellation)) _messageCancellation = null; }
    }

    private void SetMessageHeader(MailMessageSummary message)
    {
        if (_mailSubject is not null) _mailSubject.Text = message.Subject;
        if (_mailMetadata is not null) _mailMetadata.Text = $"From  {message.From}\nTo  {message.To}\n{message.Created.LocalDateTime:g}";
    }

    private IEnumerable<MenuItem> MailMenu()
    {
        if (_snapshot.Services.FirstOrDefault(s => s.Kind == ServiceKind.Mailpit && s.Status != ServiceStatus.NotInstalled) is { } mail)
        {
            yield return Menu(mail.Status == ServiceStatus.Running ? "Stop Mail Trap" : "Start Mail Trap", () => ToggleServiceAsync(ServiceKind.Mailpit), !Busy);
            yield return Menu("Restart Mail Trap", () => RunAsync("Restarting Mail Trap…", async () => { await _manager.RestartServiceAsync(mail.Id, _lifetime.Token); await ReadInboxCoreAsync(); }), !Busy);
        }
        else yield return Menu("Install Mailpit…", () => OpenInstallAsync(ServiceKind.Mailpit), !Busy);
        yield return Menu("Copy SMTP settings", CopySmtpAsync);
        yield return Menu("Import Mailpit…", () => BrowseRuntimeAsync(ServiceKind.Mailpit), !Busy);
    }

    private Task CopySmtpAsync() => CopyTextAsync($"Host: 127.0.0.1\nPort: {_snapshot.SmtpPort}\nEncryption: None\nAuthentication: None", "SMTP settings copied to clipboard.");

    private Task CopyTextAsync(string text, string confirmation)
    {
        Clipboard.SetText(text); _feedbackMessage = confirmation; Render(_snapshot); return Task.CompletedTask;
    }

    private void BuildLogs()
    {
        Heading("Activity", "Recent events from your environment.");
        _pageActions.Children.Add(ActionButton("Open logs folder", () => OpenPathAsync(Path.Combine(_snapshot.DataDirectory, "logs")), "SecondaryButtonStyle", AppIcon.Folder, "Open log folder", independent: true));
        _logOutput = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, Padding = new Thickness(16), BorderThickness = new Thickness(0) };
        AutomationProperties.SetName(_logOutput, "Recent application log"); _logOutput.SetResourceReference(Control.BackgroundProperty, "CardBrush"); _contentHost.Children.Add(Surface(_logOutput, "CardBrush", 9, new Thickness(0)));
    }

    private void BuildInstall()
    {
        Heading(_releasePhpChannel is { } channel ? $"Update PHP {channel}" : "Install runtimes", "Official releases. Verified before they run.");
        var page = new Grid(); page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); page.RowDefinitions.Add(new RowDefinition());
        var kindTabs = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (kind, label, icon) in new[] { (ServiceKind.Php, "PHP", AppIcon.Terminal), (ServiceKind.Nginx, "Nginx", AppIcon.Globe), (ServiceKind.Mailpit, "Mailpit", AppIcon.Mail) })
        {
            var tab = ActionButton(label, async () =>
            {
                if (_releaseKind != kind || _releasePhpChannel is not null) { _releaseKind = kind; _releasePhpChannel = null; _releases = []; _selectedReleaseId = null; _releaseCheckComplete = false; _releaseError = null; _installProgress = null; _renderedSection = null; Render(_snapshot); }
                if (!_releaseCheckComplete) await CheckReleasesAsync();
            }, "InstallerTabStyle");
            if (_releaseKind == kind) { tab.Tag = "Selected"; AutomationProperties.SetHelpText(tab, "Selected runtime type"); }
            tab.Margin = new Thickness(0); kindTabs.Children.Add(tab);
        }
        var tabSurface = new Border { Child = kindTabs, BorderThickness = new Thickness(0, 0, 0, 1) }; tabSurface.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush"); tabSurface.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); page.Children.Add(tabSurface); _releaseContent = new ContentControl { Padding = new Thickness(0) }; Grid.SetRow(_releaseContent, 1); page.Children.Add(_releaseContent); _contentHost.Children.Add(Surface(page, "CardBrush", 10, new Thickness(0))); _releaseSignature = string.Empty;
    }

    private void UpdateInstall()
    {
        if (_releaseContent is null) return;
        var signature = $"{_releaseKind}:{_releasePhpChannel}:{_releaseCheckComplete}:{_selectedReleaseId}:{_releaseError}:{string.Join('|', _releases.Select(r => r.Id))}:{string.Join('|', _snapshot.PhpInstallations.Select(p => $"{p.Channel}:{p.Version}"))}:{HasRuntime(ServiceKind.Nginx)}:{HasRuntime(ServiceKind.Mailpit)}";
        if (_releaseSignature != signature)
        {
            _releaseSignature = signature; _downloadPanel = null;
            _bodyButtons.RemoveAll(registration => Equals(registration.Button.Tag, "ReleaseStage"));
            var page = new StackPanel();
            if (_releaseError is { Length: > 0 }) page.Children.Add(InlineNotice(_releaseError));
            if (_releases.Count == 0)
            {
                var type = _releaseKind == ServiceKind.Php ? _releasePhpChannel is { } phpChannel ? $"PHP {phpChannel}" : "PHP" : _releaseKind == ServiceKind.Nginx ? "Nginx" : "Mailpit";
                var failed = !string.IsNullOrWhiteSpace(_releaseError);
                page.Children.Add(Empty(AppIcon.Download, failed ? "Could not load releases" : _releaseCheckComplete ? "No compatible releases found" : $"Add {type} to your workspace", failed ? "Check your connection and try again, or import an extracted runtime." : _releaseCheckComplete ? "Refresh the release list or import a downloaded distribution." : "Choose a release from the official publisher. Nothing downloads until you install.", failed ? "Retry release check" : _releaseCheckComplete ? "Refresh releases" : $"Find {type} releases", CheckReleasesAsync, "Import a downloaded runtime", () => BrowseRuntimeAsync(_releaseKind)));
            }
            else
            {
                var selected = _releases.FirstOrDefault(r => r.Id == _selectedReleaseId) ?? _releases[0]; _selectedReleaseId = selected.Id;
                var card = new StackPanel();
                var head = new DockPanel(); var refresh = ActionButton("", CheckReleasesAsync, "IconButtonStyle", AppIcon.Refresh, "Refresh official releases"); DockPanel.SetDock(refresh, Dock.Right); head.Children.Add(refresh); head.Children.Add(T("Choose a version", 14, true)); card.Children.Add(head);
                var versions = new ComboBox { ItemsSource = _releases, DisplayMemberPath = nameof(RuntimeRelease.DisplayName), SelectedItem = selected, Margin = new Thickness(0, 12, 0, 14), MinHeight = 40 };
                _releaseVersionPicker = versions;
                AutomationProperties.SetName(versions, "Official version to install"); versions.SelectionChanged += (_, _) => { if (versions.SelectedItem is RuntimeRelease release && release.Id != _selectedReleaseId) { _selectedReleaseId = release.Id; _installProgress = null; Render(_snapshot); } }; card.Children.Add(versions);
                var retained = RetainedPhpFor(selected);
                var trust = IconLabel(retained is null ? AppIcon.Check : AppIcon.Folder, retained is null ? $"Official source · {selected.DownloadUri.Host}" : "Retained locally · no download", 14); trust.Margin = new Thickness(0, 0, 0, 7); card.Children.Add(trust);
                trust.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextBrush");
                card.Children.Add(T(retained is null ? selected.IntegrityDescription ?? "Publisher verification is required before installation." : "The saved binaries and current configuration are validated before activation.", 11, muted: true));
                if (InstalledPhpFor(selected) is { } installed)
                {
                    card.Children.Add(T($"Active PHP {installed.Version}  ·  Selected PHP {PhpPatch(selected.Version)}", 12, true, margin: new Thickness(0, 15, 0, 0)));
                    if (PhpPatch(selected.Version) != installed.Version)
                    {
                        card.Children.Add(T("Sites, port, extensions, and CLI selection stay with this channel. The current patch is kept for rollback.", 11, muted: true, margin: new Thickness(0, 8, 0, 0)));
                        card.Children.Add(T("Running PHP and dependent Nginx briefly restart; stopped channels stay stopped. If activation fails, the manager attempts to restore the previous patch.", 11, muted: true, margin: new Thickness(0, 8, 0, 0)));
                    }
                }
                var reason = InstallationBlockReason(selected);
                if (reason is not null) card.Children.Add(T(reason, 12, muted: true, margin: new Thickness(0, 12, 0, 0)));
                var install = ActionButton(reason is null ? InstallActionLabel(selected) : HasInstallationFor(selected) ? "Already installed" : "Unavailable", retained is null ? () => InstallSelectedReleaseAsync(selected) : () => SwitchPhpVersionAsync(retained), "PrimaryButtonStyle", retained is null ? AppIcon.Download : AppIcon.Restart, enabled: () => !Busy && InstallationBlockReason(selected) is null); install.HorizontalAlignment = HorizontalAlignment.Left; install.Margin = new Thickness(0, 20, 0, 0); card.Children.Add(install);
                page.Children.Add(new Border { Child = card, Padding = new Thickness(18) });
                page.Children.Add(T(_releaseKind == ServiceKind.Php ? "Switch to a retained patch from the PHP runtime's actions menu." : "Updates to installed Nginx and Mailpit runtimes are not available yet.", 11, muted: true, margin: new Thickness(1, 12, 0, 0)));
                var import = ActionButton("Import extracted runtime…", () => BrowseRuntimeAsync(_releaseKind), "QuietButtonStyle", AppIcon.Folder);
                import.HorizontalAlignment = HorizontalAlignment.Left;
                import.Margin = new Thickness(0, 8, 0, 0);
                page.Children.Add(import);
            }
            var progress = new StackPanel(); _installPhase = T(string.Empty, 12, true); AutomationProperties.SetLiveSetting(_installPhase, AutomationLiveSetting.Polite); progress.Children.Add(_installPhase);
            _downloadBar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 5, Margin = new Thickness(0, 10, 0, 8) }; AutomationProperties.SetName(_downloadBar, "Installation progress"); progress.Children.Add(_downloadBar); _installBytes = T(string.Empty, 10, muted: true); progress.Children.Add(_installBytes);
            _cancelDownload = ActionButton("Cancel download", () => { _installCancellation?.Cancel(); return Task.CompletedTask; }, "QuietButtonStyle", independent: true); _cancelDownload.HorizontalAlignment = HorizontalAlignment.Left; _cancelDownload.Margin = new Thickness(0, 6, 0, 0); progress.Children.Add(_cancelDownload);
            _downloadPanel = Surface(progress, "CardBrush", 9, new Thickness(15)); _downloadPanel.Margin = new Thickness(0, 14, 0, 0); page.Children.Add(_downloadPanel);
            _releaseContent.Content = Scroll(page);
        }
        if (_downloadPanel is not null)
        {
            _downloadPanel.Visibility = _installProgress is null ? Visibility.Collapsed : Visibility.Visible;
            if (_installProgress is { } progress)
            {
                _installPhase!.Text = progress.Phase;
                _downloadBar!.IsIndeterminate = _installCancellation is not null && progress.TotalBytes is not > 0;
                _downloadBar.Value = progress.TotalBytes is > 0 ? Math.Clamp(progress.BytesReceived * 100d / progress.TotalBytes.Value, 0, 100) : 0;
                _installBytes!.Text = progress.TotalBytes is > 0 ? $"{FormatBytes(progress.BytesReceived)} of {FormatBytes(progress.TotalBytes.Value)}" : progress.BytesReceived > 0 ? $"{FormatBytes(progress.BytesReceived)} downloaded" : string.Empty;
                _cancelDownload!.Visibility = _installCancellation is null ? Visibility.Collapsed : Visibility.Visible;
            }
        }
        if (_releaseVersionPicker is not null) _releaseVersionPicker.IsEnabled = !Busy;
    }

    private void BuildSettings()
    {
        Heading("Settings", "Appearance, command-line PHP and workspace files.");
        var settings = new StackPanel();
        var appearance = new StackPanel(); appearance.Children.Add(T("Appearance", 14, true)); appearance.Children.Add(T("Choose a theme or follow Windows.", 11, muted: true, margin: new Thickness(0, 5, 0, 14)));
        var themes = new UniformGrid { Columns = 3 };
        foreach (var (theme, icon, label) in new[] { (ThemePreference.System, AppIcon.Monitor, "System"), (ThemePreference.Light, AppIcon.Sun, "Light"), (ThemePreference.Dark, AppIcon.Moon, "Dark") })
        {
            var button = ActionButton(label, () => RunAsync("Changing appearance…", () => _manager.SetThemeAsync(theme, _lifetime.Token)), "SecondaryButtonStyle", icon, $"Use {label.ToLowerInvariant()} theme"); button.Margin = new Thickness(0, 0, theme == ThemePreference.Dark ? 0 : 7, 0); themes.Children.Add(button); _themeButtons[theme] = button;
        }
        appearance.Children.Add(themes); var appearanceSurface = Surface(appearance, "CardBrush", 10, new Thickness(17)); appearanceSurface.Margin = new Thickness(0, 0, 0, 12); settings.Children.Add(appearanceSurface);
        var cli = new StackPanel(); cli.Children.Add(T("Command-line PHP", 14, true)); cli.Children.Add(T("Use your selected PHP version in new terminal sessions.", 11, muted: true, margin: new Thickness(0, 5, 0, 13)));
        if (_snapshot.PhpInstallations.Count > 0)
        {
            var row = new DockPanel(); var label = T("Default version", 12); label.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(label);
            _defaultPhpPicker = new ComboBox { ItemsSource = _snapshot.PhpInstallations.Select(p => p.Channel).Distinct().ToArray(), SelectedItem = _snapshot.DefaultPhpChannel, Width = 100, HorizontalAlignment = HorizontalAlignment.Right };
            AutomationProperties.SetName(_defaultPhpPicker, "Default command-line PHP version"); _defaultPhpPicker.SelectionChanged += async (_, _) => { if (!_updatingInputs && _defaultPhpPicker.SelectedItem is string channel && channel != _snapshot.DefaultPhpChannel) await RunAsync("Changing default PHP…", () => _manager.SetDefaultPhpAsync(channel, _lifetime.Token)); }; row.Children.Add(_defaultPhpPicker); cli.Children.Add(row);
        }
        else cli.Children.Add(T("Install PHP to choose a command-line version.", 12, muted: true));
        _pathSetting = new CheckBox { Content = "Make PHP available in terminals", Margin = new Thickness(0, 11, 0, 0), IsChecked = _snapshot.PathIntegrationEnabled };
        AutomationProperties.SetName(_pathSetting, "Enable PHP in the user PATH"); _pathSetting.Click += async (_, _) => { if (!_updatingInputs) { var enabled = _pathSetting.IsChecked == true; await RunAsync("Updating terminal integration…", () => _manager.SetPathIntegrationAsync(enabled, _lifetime.Token)); } }; cli.Children.Add(_pathSetting); settings.Children.Add(Surface(cli, "CardBrush", 10, new Thickness(17)));
        var storage = new DockPanel { Margin = new Thickness(1, 19, 0, 0) }; var folder = ActionButton("Open folder", () => OpenPathAsync(_snapshot.DataDirectory), "QuietButtonStyle", AppIcon.Folder, independent: true); DockPanel.SetDock(folder, Dock.Right); storage.Children.Add(folder); var storageText = new StackPanel(); storageText.Children.Add(T("Workspace files", 12, true)); var path = T(_snapshot.DataDirectory, 10, muted: true, margin: new Thickness(0, 5, 0, 0));  storageText.Children.Add(path); storage.Children.Add(storageText); settings.Children.Add(storage);
        _contentHost.Children.Add(Scroll(settings));
    }

    private void UpdateSettings()
    {
        _updatingInputs = true;
        foreach (var (theme, button) in _themeButtons)
        {
            var selected = theme == _snapshot.Theme;
            button.Content = IconLabel(selected ? AppIcon.Check : theme == ThemePreference.System ? AppIcon.Monitor : theme == ThemePreference.Light ? AppIcon.Sun : AppIcon.Moon, theme.ToString(), 15);
            AutomationProperties.SetName(button, $"Use {theme.ToString().ToLowerInvariant()} theme{(selected ? " (selected)" : "")}");
            button.SetResourceReference(Control.BackgroundProperty, theme == _snapshot.Theme ? "AccentSoftBrush" : "CardBrush");
            button.SetResourceReference(Control.BorderBrushProperty, theme == _snapshot.Theme ? "AccentBrush" : "BorderBrush");
        }
        if (_pathSetting is not null) { _pathSetting.IsChecked = _snapshot.PathIntegrationEnabled; _pathSetting.IsEnabled = !Busy && _snapshot.PhpInstallations.Count > 0; }
        if (_defaultPhpPicker is not null) { _defaultPhpPicker.SelectedItem = _snapshot.DefaultPhpChannel; _defaultPhpPicker.IsEnabled = !Busy; }
        _updatingInputs = false;
    }

    internal async Task PrepareSmokeSectionAsync(string section, bool checkReleases = false)
    {
        SetSmokeSection(section);
        if (section is "Mail Trap" or "Logs") { await _pageRefresher.RefreshNowAsync(); await _pageRefresher.RefreshNowAsync(); }
        else if (section == "Updates" && checkReleases) await CheckReleasesAsync();
    }

    internal Task PreparePhpUpdatesForSmokeAsync(string channel) => OpenPhpUpdatesAsync(channel);

    internal async Task SetSitePhpForSmokeAsync(string hostname, string channel)
    {
        var picker = _siteVersionPickers.FirstOrDefault(control => AutomationProperties.GetName(control) == $"PHP version for {hostname}")
            ?? throw new InvalidOperationException("Site version picker not found.");
        picker.SelectedValue = channel;
        var deadline = Environment.TickCount64 + 15000;
        while (Busy && Environment.TickCount64 < deadline) await Task.Delay(20);
        if (Busy || _snapshot.Sites.FirstOrDefault(site => site.Hostname == hostname)?.PhpChannel != channel)
            throw new InvalidOperationException(_error ?? "The site version selection did not commit.");
    }

    internal void SetMailSearchForSmoke(string query)
    {
        if (_mailSearch is null) throw new InvalidOperationException("Open a nonempty running inbox before testing search.");
        _mailSearch.Text = query;
    }

    private async Task NavigateAsync(string section)
    {
        _feedbackMessage = null; _section = section; Render(_snapshot);
        try { await _manager.SetLastSectionAsync(section, _lifetime.Token); }
        catch (Exception exception) { ShowError(exception.Message); }
        if (section is "Mail Trap" or "Logs") await _pageRefresher.RefreshNowAsync();
        if (section == "Updates" && !_releaseCheckComplete) await CheckReleasesAsync();
    }

    private async Task OpenInstallAsync(ServiceKind kind)
    {
        if (_releaseKind != kind || _releasePhpChannel is not null) { _releaseKind = kind; _releasePhpChannel = null; _releases = []; _releaseCheckComplete = false; _selectedReleaseId = null; _installProgress = null; _releaseError = null; }
        _renderedSection = null; await NavigateAsync("Updates");
        if (!_releaseCheckComplete) await CheckReleasesAsync();
    }

    private async Task OpenPhpUpdatesAsync(string channel)
    {
        _releaseKind = ServiceKind.Php; _releasePhpChannel = channel; _releases = []; _releaseCheckComplete = false;
        _selectedReleaseId = null; _installProgress = null; _releaseError = null; _renderedSection = null;
        await NavigateAsync("Updates");
        await CheckReleasesAsync();
    }

    private async Task BrowseRuntimeAsync(ServiceKind kind)
    {
        using var hold = HoldFocus();
        var executable = kind switch { ServiceKind.Php => "php.exe", ServiceKind.Nginx => "nginx.exe", _ => "mailpit.exe" };
        var picker = new OpenFileDialog { Title = $"Select {executable} from an extracted distribution", Filter = $"{executable}|{executable}|Executables|*.exe", CheckFileExists = true, Multiselect = false };
        if (picker.ShowDialog(this) != true) return;
        await RunAsync($"Validating {kind} runtime…", () => kind switch
        {
            ServiceKind.Php => _manager.RegisterPhpAsync(picker.FileName),
            ServiceKind.Nginx => _manager.RegisterNginxAsync(picker.FileName),
            _ => _manager.RegisterMailpitAsync(picker.FileName),
        });
    }

    private async Task ShowCreateSiteAsync()
    {
        if (_snapshot.PhpInstallations.Count == 0) { ShowError("Add a PHP runtime before creating a managed PHP site."); return; }
        using var hold = HoldFocus();
        var dialog = new SiteDialog(_snapshot.PhpInstallations.Select(p => p.Channel).Distinct().ToArray()) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Site is null) return;
        await RunAsync("Creating site and validating Nginx…", () => _manager.SaveSiteAsync(dialog.Site));
    }

    private Task ShowExtensionsAsync(string channel)
    {
        using var hold = HoldFocus();
        var dialog = new ExtensionsDialog(_manager, channel, _lifetime.Token) { Owner = this };
        dialog.ShowDialog();
        return Task.CompletedTask;
    }

    private Task CheckReleasesAsync() => RunAsync("Reading official release metadata…", async () =>
    {
        _releaseError = null;
        try
        {
            var releases = await _manager.GetAvailableReleasesAsync(_releaseKind, _lifetime.Token);
            _releases = _releaseKind == ServiceKind.Php && _releasePhpChannel is { } channel
                ? releases.Where(release => ReleaseChannel(release) == channel).OrderByDescending(release => ParsePhpVersion(release.Version)).ToArray()
                : releases;
            _releaseCheckComplete = true;
            _selectedReleaseId = _releases.FirstOrDefault()?.Id;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _releaseCheckComplete = true;
            _releaseError = $"Could not read official release metadata: {exception.Message} Check your network connection and retry, or import an extracted official runtime.";
        }
    });

    private Task InstallSelectedReleaseAsync(RuntimeRelease release) => RunAsync($"Installing {release.DisplayName}…", async () =>
    {
        if (InstallationBlockReason(release) is { } reason) throw new InvalidOperationException(reason);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _installCancellation = cancellation;
        _releaseError = null;
        _installProgress = new RuntimeInstallProgress("Preparing download…");
        Render(_snapshot);
        try
        {
            var progress = new Progress<RuntimeInstallProgress>(update =>
            {
                if (!ReferenceEquals(_installCancellation, cancellation)) return;
                var phaseChanged = update.Phase != _installProgress?.Phase;
                _installProgress = update;
                if (_shutdown) return;
                var now = Environment.TickCount64;
                if (phaseChanged || now - _lastProgressRender >= 100)
                {
                    _lastProgressRender = now;
                    Render(_manager.Snapshot);
                }
            });
            await _manager.InstallReleaseAsync(release.Id, progress, cancellation.Token);
            _installProgress = new RuntimeInstallProgress($"Installed {release.DisplayName}", _installProgress?.BytesReceived ?? 0, _installProgress?.TotalBytes);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { _installProgress = new RuntimeInstallProgress("Installation cancelled. Any completed registration is shown in the runtime list."); }
        catch (Exception exception)
        {
            _releaseError = $"Installation failed: {exception.Message} Retry the official download or import an extracted distribution.";
            _installProgress = new RuntimeInstallProgress("Installation failed");
        }
        finally { _installCancellation = null; }
    });

    private string? InstallationBlockReason(RuntimeRelease release)
    {
        if (release.Kind == ServiceKind.Php)
        {
            if (InstalledPhpFor(release) is { } installed && installed.Version == PhpPatch(release.Version))
                return $"PHP {installed.Version} is already active for this channel.";
            if (RetainedPhpFor(release) is not null) return null;
        }
        else if (_snapshot.Services.Any(s => s.Kind == release.Kind && s.Status != ServiceStatus.NotInstalled))
            return "Already installed. Updates are not available yet.";
        var checksumAvailable = release.Sha256 is { Length: 64 } digest && digest.All(Uri.IsHexDigit);
        var signatureAvailable = release.SignatureUri is { IsAbsoluteUri: true } signature && signature.Scheme == Uri.UriSchemeHttps;
        if (!checksumAvailable && !signatureAvailable)
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

    private string InstallActionLabel(RuntimeRelease release) => RetainedPhpFor(release) is { } retained ? $"Switch to PHP {retained.Version}" : InstalledPhpFor(release) is { } installed
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
        var page = IsVisible && !_shutdown && (_section == "Logs" || (_section == "Mail Trap" && MailIsRunning)) ? _section : null;
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
                var lines = await _manager.GetLogLinesAsync(token);
                if (token.IsCancellationRequested || !IsVisible || _section != page || Busy) return;
                if (!_logLines.SequenceEqual(lines)) _logLines = lines;
            }
            if (token.IsCancellationRequested || !IsVisible || _section != page) return;
            if (_autoRefreshError is not null && _error == _autoRefreshError) _error = null;
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
                if (_error is null) ShowError(exception.Message);
            }
        }
    }

    private void UpdateLogOutput()
    {
        if (_logOutput is null || ReferenceEquals(_logOutput.Tag, _logLines) || _logOutput.SelectionLength > 0) return;
        var offset = _logOutput.VerticalOffset;
        var caret = _logOutput.CaretIndex;
        _logOutput.Text = _logLines.Count == 0 ? "No activity recorded yet." : string.Join(Environment.NewLine, _logLines);
        _logOutput.Tag = _logLines;
        _logOutput.CaretIndex = Math.Min(caret, _logOutput.Text.Length);
        _logOutput.ScrollToVerticalOffset(offset);
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
        _feedbackMessage = null;
        _error = null;
        Render(_manager.Snapshot);
        try { await action(); }
        catch (Exception exception) { _error = exception.Message; }
        finally { _operation = null; Render(_manager.Snapshot); }
    }

    public void ShowError(string message) { _error = message; Render(_manager.Snapshot); }
    public void BeginShutdown()
    {
        _shutdown = true;
        _pageRefresher.Dispose();
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
        _ignoreDeactivationUntil = Environment.TickCount64 + 1500;
        var command = new ProcessStartInfo("notepad.exe") { UseShellExecute = false };
        command.ArgumentList.Add(path);
        try { Process.Start(command); }
        catch { _ignoreDeactivationUntil = 0; throw; }
        return Task.CompletedTask;
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
        : HasRuntime(release.Kind);
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
        var row = new DockPanel();
        if (action is not null && handler is not null) { var button = ActionButton(action, handler, "QuietButtonStyle"); DockPanel.SetDock(button, Dock.Right); row.Children.Add(button); }
        var description = T(text, 12, muted: true); description.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(description);
        var surface = Surface(row, "HoverBrush", 8, new Thickness(11)); surface.Margin = new Thickness(0, 0, 0, 13); return surface;
    }

    private static Border Badge(string label, string background, Thickness margin)
    {
        var badge = new Border { Child = T(label, 9), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 3, 6, 3), Margin = margin, VerticalAlignment = VerticalAlignment.Center };
        badge.SetResourceReference(Border.BackgroundProperty, background); return badge;
    }
    private static Border RowSurface(UIElement content) { var row = Surface(content, "CardBrush", 9, new Thickness(13, 13, 9, 13)); row.Margin = new Thickness(0, 0, 0, 9); return row; }
    private static Border Surface(UIElement content, string background, double radius, Thickness padding)
    {
        var border = new Border { Child = content, CornerRadius = new CornerRadius(radius), Padding = padding, BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, background); border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); return border;
    }
    private static ScrollViewer Scroll(UIElement content) => new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 3, 4) };
    private static TextBlock T(string text, double size = 13, bool bold = false, bool muted = false, Thickness? margin = null)
    {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = margin ?? new Thickness(0) };
        block.SetResourceReference(TextBlock.ForegroundProperty, muted ? "MutedBrush" : "TextBrush"); return block;
    }
    private sealed record PhpRow(PhpInstallation Installation, TextBlock Status, Button Toggle);
    private sealed record InlineService(string Title, TextBlock Status);
    private sealed class Scope(Action dispose) : IDisposable { public void Dispose() => dispose(); }
}
