using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PhpDevTray.Core;
using Ui = Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;

namespace PhpDevTray.App;

internal sealed partial class ManagerWindow
{
    private readonly Ui.NavigationView _shellNavigation;
    private readonly Ui.TitleBar _titleBar;
    private readonly Grid _pageRoot;
    private readonly OperationTracker _operationTracker;
    private readonly NotificationCenter _notifications;
    private readonly ActivityBuffer _activity;
    private readonly OperationStatusBar _statusBar;
    private readonly Wpf.Ui.SnackbarService _snackbars = new();
    internal FrameworkElement PageContent => _pageRoot;

    public ManagerWindow(IEnvironmentManager manager, IStartupRegistration? startupRegistration = null)
    {
        _manager = manager; _snapshot = manager.Snapshot;
        _operationTracker = manager.Operations ?? new(); _notifications = manager.Notifications ?? new(); _activity = manager.Activity ?? new();
        _startupRegistration = startupRegistration ?? new StartupRegistration(_snapshot.DataDirectory);
        _section = Sections.Contains(_snapshot.LastSection) ? _snapshot.LastSection : "Overview";
        Title = "PHP DevTray"; Width = 1120; Height = 800; MinWidth = 560; MinHeight = 500;
        Icon = AppLogo.WindowIcon;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = Ui.WindowBackdropType.Mica;
        ShowInTaskbar = true; ResizeMode = ResizeMode.CanResize;
        SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        var shell = new Grid(); shell.SetResourceReference(Panel.BackgroundProperty, "ApplicationBackgroundBrush"); Content = shell;
        shell.RowDefinitions.Add(new RowDefinition()); shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
        _titleBar = new Ui.TitleBar
        {
            Title = "PHP DevTray", Height = 32, VerticalAlignment = VerticalAlignment.Top,
            Icon = new Ui.ImageIcon { Source = AppLogo.WindowIcon }, ForceShutdown = false
        };
        _shellNavigation = new Ui.NavigationView
        {
            PaneDisplayMode = Ui.NavigationViewPaneDisplayMode.Left,
            OpenPaneLength = 240, CompactPaneLength = 48, IsPaneOpen = true,
            IsBackButtonVisible = Ui.NavigationViewBackButtonVisible.Collapsed, IsPaneToggleVisible = true,
            IsTopSeparatorVisible = false, IsFooterSeparatorVisible = false,
            FrameMargin = new Thickness(0, 32, 0, 0), TitleBar = _titleBar,
            Transition = Wpf.Ui.Animations.Transition.None, TransitionDuration = 0,
            PaneTitle = null
        };
        AutomationProperties.SetName(_shellNavigation, "Main navigation");
        AddNavigation("Overview", "Overview", AppIcon.Home);
        AddNavigation("Sites", "Sites", AppIcon.Sites);
        AddNavigation("Caddy", "Web server", AppIcon.Server);
        AddNavigation("PHP", "PHP", AppIcon.Php);
        AddNavigation("Mail Trap", "Mail Trap", AppIcon.Mail);
        AddNavigation("Logs", "Activity", AppIcon.Logs);
        _shellNavigation.MenuItems.Add(new Ui.NavigationViewItemSeparator());
        AddNavigation("Updates", "Install runtimes", AppIcon.Download);
        AddNavigation("Settings", "Settings", AppIcon.Settings, footer: true);
        _overall = T("", 12, muted: true);
        _overall.VerticalAlignment = VerticalAlignment.Center;
        _overall.TextWrapping = TextWrapping.NoWrap;
        _overall.TextTrimming = TextTrimming.CharacterEllipsis;
        _overall.Margin = new Thickness(0, 0, 16, 0);
        shell.Children.Add(_shellNavigation);
        shell.Children.Add(_titleBar);

        _globalAction = new Button { Height = 32, Padding = new Thickness(8, 3, 8, 3), FontSize = 12, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _globalAction.SetResourceReference(StyleProperty, "SecondaryButtonStyle");
        _globalAction.Click += async (_, _) => await RunAsync(AnyRunning ? "Stopping services…" : "Starting services…",
            () => AnyRunning ? _manager.StopAllAsync(_lifetime.Token) : _manager.StartAllAsync(_lifetime.Token));
        var globalActions = new StackPanel { Orientation = Orientation.Horizontal };
        _globalRestart = new Button { Content = IconLabel(AppIcon.Restart, "Restart all", 15), Height = 32, Padding = new Thickness(8, 3, 8, 3), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        _globalRestart.SetResourceReference(StyleProperty, "SecondaryButtonStyle");
        AutomationProperties.SetName(_globalRestart, "Restart all running services");
        _globalRestart.Click += async (_, _) => await RestartRunningServicesAsync();

        var page = _pageRoot = new Grid { Margin = new Thickness(32, 16, 32, 12) };
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition());
        var toolbar = new Grid { Height = 32, Margin = new Thickness(0, 0, 0, 24) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition());
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.Children.Add(_overall);
        Grid.SetColumn(globalActions, 1); toolbar.Children.Add(globalActions);
        page.Children.Add(toolbar);

        var heading = new Grid { Margin = new Thickness(0, 0, 0, 24) };
        heading.ColumnDefinitions.Add(new ColumnDefinition()); heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); heading.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var titles = new StackPanel();
        _pageTitle = T("", 28, true);
        _pageDescription = T("", 14, muted: true, margin: new Thickness(0, 8, 0, 0));
        titles.Children.Add(_pageTitle); titles.Children.Add(_pageDescription); heading.Children.Add(titles);
        _pageActions = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(_pageActions, 1); heading.Children.Add(_pageActions);
        Grid.SetRow(heading, 1); page.Children.Add(heading);
        _contentHost = new Grid { ClipToBounds = true }; Grid.SetRow(_contentHost, 2); page.Children.Add(_contentHost);
        globalActions.Children.Add(_globalRestart); globalActions.Children.Add(_globalAction);
        _statusBar = new OperationStatusBar(_operationTracker, _notifications);
        _operationText = _statusBar.Message;
        _statusBar.Tick += (_, _) => { if (IsVisible && _section == "Logs" && !_updatesPaused && _manager.Activity is not null) UpdateLogOutput(); };
        Grid.SetRow(_statusBar, 1); shell.Children.Add(_statusBar);
        var snackbarPresenter = new Ui.SnackbarPresenter { VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 400, Margin = new Thickness(16) };
        var snackbarStyle = new Style(typeof(Ui.Snackbar), snackbarPresenter.TryFindResource(typeof(Ui.Snackbar)) as Style);
        snackbarStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("TextBrush")));
        snackbarStyle.Setters.Add(new Setter(Ui.Snackbar.ContentForegroundProperty, new DynamicResourceExtension("TextBrush")));
        snackbarStyle.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("CardBrush")));
        snackbarStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 8, 12, 8)));
        snackbarStyle.Setters.Add(new Setter(Control.FontSizeProperty, 12d));
        snackbarStyle.Setters.Add(new Setter(MinHeightProperty, 0d));
        snackbarStyle.Setters.Add(new Setter(MinWidthProperty, 0d));
        snackbarStyle.Setters.Add(new Setter(MaxWidthProperty, 400d));
        snackbarPresenter.Resources[typeof(Ui.Snackbar)] = snackbarStyle;
        // WPF UI's saturated snackbar palette needs darker fills for white text.
        foreach (var (name, hex) in new[] { ("Red", "#B42318"), ("Green", "#146C43"), ("Orange", "#8A5700"), ("Blue", "#005A9E") })
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            snackbarPresenter.Resources[$"Palette{name}Color"] = color;
            snackbarPresenter.Resources[$"Palette{name}Brush"] = new SolidColorBrush(color);
        }
        foreach (var buttonType in new[] { typeof(System.Windows.Controls.Button), typeof(Ui.Button) })
        {
            var dismissStyle = new Style(buttonType, snackbarPresenter.TryFindResource(buttonType) as Style);
            dismissStyle.Setters.Add(new Setter(AutomationProperties.NameProperty, "Dismiss notification"));
            snackbarPresenter.Resources[buttonType] = dismissStyle;
        }
        shell.Children.Add(snackbarPresenter); _snackbars.SetSnackbarPresenter(snackbarPresenter);
        _notifications.Published += OnNotification;
        _shellNavigation.SetPageProviderService(new NavigationPages(this));
        _shellNavigation.SelectionChanged += async (_, _) =>
        {
            if (!_suppressNavigationEffects && _shellNavigation.SelectedItem is Ui.NavigationViewItem item)
                await (_navigationTask = AfterNavigationAsync(item.TargetPageTag));
        };
        _shellNavigation.ApplyTemplate();
        page.SizeChanged += (_, _) =>
        {
            var narrow = page.ActualWidth < 640;
            Grid.SetColumn(_pageActions, narrow ? 0 : 1); Grid.SetRow(_pageActions, narrow ? 1 : 0);
            _pageActions.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            _pageActions.Margin = narrow ? new Thickness(0, 16, 0, 0) : new Thickness(16, 0, 0, 0);
        };
        shell.SizeChanged += (_, _) =>
        {
            var narrow = shell.ActualWidth < 900;
            _shellNavigation.SetCurrentValue(Ui.NavigationView.IsPaneOpenProperty, !narrow);
            page.Margin = new Thickness(narrow ? 24 : 32, 16, narrow ? 24 : 32, 12);
        };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) Hide(); else _restoreState = WindowState; };
        _pageRefresher = new PageAutoRefresher(Dispatcher, RefreshVisiblePageAsync);
        IsVisibleChanged += (_, _) => UpdatePageRefresh();
        Activated += async (_, _) => { if (_section == "Settings") await ReadStartupSettingAsync(); };
        Closing += OnClosing;
        PreviewKeyDown += (_, args) =>
        {
            if (_section == "Logs" && args.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            { _logOutput?.ShowSearch(); args.Handled = true; return; }
            if (_section == "Logs" && args.Key == Key.F3)
            { _logOutput?.FindMatch(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); args.Handled = true; return; }
            if (args.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && _mailSearch is not null)
            { _mailSearch.Focus(); _mailSearch.SelectAll(); args.Handled = true; }
        };
        SetSmokeSection(_section);
    }

    private void AddNavigation(string key, string label, AppIcon icon, bool footer = false)
    {
        var item = new Ui.NavigationViewItem { Content = label, TargetPageTag = key, TargetPageType = NavigationPages.PageType(key), Tag = "MainNavigation", Icon = UiIcon.NavigationIcon(icon), ToolTip = label };
        AutomationProperties.SetName(item, label);
        item.Click += async (_, _) => await NavigateAsync(key);
        _navigation[key] = item;
        (footer ? _shellNavigation.FooterMenuItems : _shellNavigation.MenuItems).Add(item);
    }

    private void BuildOverview()
    {
        Heading("Overview", "Your local development environment at a glance.");
        _serviceLabels.Clear(); _serviceToggles.Clear();
        var rows = new StackPanel();
        rows.Children.Add(T("Services", 20, true, margin: new Thickness(0, 0, 0, 12)));
        AddOverviewService(rows, ServiceKind.Caddy, "Web server", "Caddy serves your local sites over HTTPS.", AppIcon.Server, "Caddy");
        AddOverviewService(rows, ServiceKind.Php, "PHP", "Engines available to your sites and command line.", AppIcon.Php, "PHP");
        AddOverviewService(rows, ServiceKind.Mailpit, "Mail Trap", "Capture development email in a local inbox.", AppIcon.Mail, "Mail Trap");
        rows.Children.Add(T("Workspace", 20, true, margin: new Thickness(0, 28, 0, 12)));
        var links = new WrapPanel();
        links.Children.Add(ActionButton("Manage sites", () => NavigateAsync("Sites"), "SecondaryButtonStyle", AppIcon.Sites));
        links.Children.Add(ActionButton("Install runtimes", () => NavigateAsync("Updates"), "SecondaryButtonStyle", AppIcon.Download));
        rows.Children.Add(links);
        rows.Children.Add(T("Closing this window keeps services running. Use Exit in the tray menu to stop them.", 14, muted: true, margin: new Thickness(0, 24, 0, 0)));
        _contentHost.Children.Add(Scroll(rows));
        UpdateOverview();
    }

    private void AddOverviewService(Panel parent, ServiceKind kind, string title, string description, AppIcon icon, string section)
    {
        var labels = new StackPanel(); labels.Children.Add(T(title, 16, true));
        labels.Children.Add(T(description, 14, muted: true, margin: new Thickness(0, 4, 0, 0)));
        var status = T("", 12, muted: true, margin: new Thickness(0, 8, 0, 0)); labels.Children.Add(status); _serviceLabels[kind] = status;
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        var toggle = ActionButton("Start", async () => { if (kind == ServiceKind.Php) await ToggleAllPhpAsync(); else await ToggleServiceAsync(kind); },
            enabled: () => !Busy && (HasRuntime(kind) || kind == ServiceKind.Php && HasRuntime(ServiceKind.FrankenPhp)));
        _serviceToggles[kind] = toggle; controls.Children.Add(toggle);
        controls.Children.Add(RestartButton(() => RestartOverviewServiceAsync(kind),
            () => OverviewServices(kind).Any(service => service.Status == ServiceStatus.Running), "Restart " + title));
        controls.Children.Add(ActionButton("", () => NavigateAsync(section), "IconButtonStyle", AppIcon.ChevronRight, "Open " + title));
        var card = RuntimeRow(labels, controls, icon);
        parent.Children.Add(card);
    }

    private void UpdateOverview()
    {
        if (_section != "Overview") return;
        foreach (var (kind, label) in _serviceLabels)
        {
            var services = _snapshot.Services.Where(service => (service.Kind == kind || kind == ServiceKind.Php && service.Kind == ServiceKind.FrankenPhp) && service.Status != ServiceStatus.NotInstalled).ToArray();
            var running = services.Any(service => service.Status == ServiceStatus.Running);
            var state = services.Length == 0 ? ServiceStatus.NotInstalled : services.Any(service => service.Status == ServiceStatus.Failed) ? ServiceStatus.Failed : running ? ServiceStatus.Running : ServiceStatus.Stopped;
            label.Text = kind == ServiceKind.Php && running ? $"{services.Count(service => service.Status == ServiceStatus.Running)} running" : StatusLabel(state);
            label.SetResourceReference(TextBlock.ForegroundProperty, StatusBrush(state));
            var toggle = _serviceToggles[kind]; toggle.Content = IconLabel(running ? AppIcon.Stop : AppIcon.Play, running ? "Stop" : "Start", 14);
            toggle.IsEnabled = !Busy && services.Length > 0;
            AutomationProperties.SetName(toggle, $"{(running ? "Stop" : "Start")} {(kind == ServiceKind.Php ? "all PHP runtimes" : kind.ToString())}");
        }
    }

    private static Ui.CardControl SettingsRow(StackPanel header, FrameworkElement control, AppIcon icon, bool showIcon = false)
    {
        var card = new Ui.CardControl
        {
            Header = header, Content = control, Icon = showIcon ? UiIcon.NavigationIcon(icon) : null,
            Margin = new Thickness(0, 0, 0, 4), MinHeight = 72, Padding = new Thickness(16), Focusable = false, IsTabStop = false
        };
        AutomationProperties.SetName(card, header.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? "Setting");
        card.SizeChanged += (_, _) => header.MaxWidth = Math.Max(40, card.ActualWidth - control.ActualWidth - 96);
        return card;
    }

    private static StackPanel SettingHeader(string title, string description)
    {
        var header = new StackPanel(); header.Children.Add(T(title, 14));
        if (description.Length > 0) header.Children.Add(T(description, 12, muted: true, margin: new Thickness(0, 4, 0, 0)));
        return header;
    }
}
