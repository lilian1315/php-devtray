using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using PhpDevTray.Core;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace PhpDevTray.App;

internal sealed partial class ManagerWindow
{
    private sealed record SiteRow(WebSite Site, TextBlock Status, Ellipse Indicator, TextBlock Detail, Button Activation);
    private readonly Dictionary<string, SiteRow> _siteRows = new(StringComparer.OrdinalIgnoreCase);
    private UIElement? _sitesStartNotice;
    private (string Hostname, string Label)? _siteActivation;

    private void BuildSites()
    {
        Heading("Sites", "Activation changes apply immediately while Caddy is running.");
        var runtimes = _snapshot.Services.Where(s => s.Kind is ServiceKind.FrankenPhp or ServiceKind.Php && s.Status != ServiceStatus.NotInstalled).ToArray();
        _pageActions.Children.Add(ActionButton("Add site", () => CreateSiteAsync(runtimes), "PrimaryButtonStyle", AppIcon.Plus,
            enabled: () => !Busy && runtimes.Length > 0));
        var rows = new StackPanel();
        if (runtimes.Length == 0)
            rows.Children.Add(InlineNotice("Install FrankenPHP or PHP NTS to add your first site.", "Install runtimes", () => OpenInstallAsync(ServiceKind.FrankenPhp)));
        _sitesStartNotice = null;
        if (!HasRuntime(ServiceKind.Caddy))
            rows.Children.Add(InlineNotice("Caddy serves HTTPS for all sites.", "Install Caddy", () => OpenInstallAsync(ServiceKind.Caddy)));
        else if (_snapshot.Sites.Any(site => site.Enabled))
        {
            _sitesStartNotice = InlineNotice("Start Caddy to serve your enabled sites.", "Start Caddy",
                () => RunAsync("Starting Caddy…", () => _manager.StartServiceAsync("caddy", _lifetime.Token)));
            rows.Children.Add(_sitesStartNotice);
        }
        if (_snapshot.Sites.Count == 0 && runtimes.Length > 0 && HasRuntime(ServiceKind.Caddy))
            rows.Children.Add(InlineNotice("Add a site to connect a local hostname to a folder and a PHP runtime."));
        foreach (var site in _snapshot.Sites.OrderBy(s => s.Hostname))
        {
            var documentRoot = _snapshot.SiteRoots.GetValueOrDefault(site.Hostname);
            var row = new StackPanel();
            var header = new DockPanel();
            var actions = MoreButton($"{site.Hostname} actions", () => [
                Menu("Rename hostname…", () => RenameSiteAsync(site), !Busy),
                Menu("Edit Caddyfile", () => OpenEditorAsync(Path.Combine(_snapshot.DataDirectory, "user", "caddy", "sites", site.CaddyfileName))),
                Menu("Open site", () => OpenPathAsync("https://" + site.Hostname), SiteIsServing(site)),
                Menu("Open document root", () => OpenPathAsync(documentRoot!), documentRoot is not null)
            ]);
            DockPanel.SetDock(actions, Dock.Right);
            header.Children.Add(actions);
            var status = T("", 11, true);
            AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
            var indicator = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 6, 0) };
            var badgeContent = new StackPanel { Orientation = Orientation.Horizontal };
            badgeContent.Children.Add(indicator);
            badgeContent.Children.Add(status);
            var badge = new Border { Child = badgeContent, Padding = new Thickness(8, 4, 8, 4), CornerRadius = new CornerRadius(6),
                Margin = new Thickness(10, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            badge.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
            DockPanel.SetDock(badge, Dock.Right);
            header.Children.Add(badge);
            var hostname = T(site.Hostname, 15, true);
            hostname.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(hostname);
            row.Children.Add(header);
            if (documentRoot is not null)
                row.Children.Add(T(documentRoot, 12, muted: true, margin: new Thickness(0, 0, 0, 4)));
            var detail = T("", 12, muted: true);
            row.Children.Add(detail);
            var controls = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            var picker = new ComboBox { ItemsSource = runtimes, DisplayMemberPath = nameof(ServiceSnapshot.DisplayName),
                SelectedValuePath = nameof(ServiceSnapshot.Id), SelectedValue = site.RuntimeId,
                MinWidth = 150, MaxWidth = 205, MinHeight = 40, Margin = new Thickness(0, 0, 8, 4) };
            AutomationProperties.SetName(picker, $"PHP runtime for {site.Hostname}");
            picker.SelectionChanged += async (_, _) =>
            {
                if (picker.SelectedValue is not string id || id == site.RuntimeId) return;
                picker.IsEnabled = false;
                await RunAsync($"Switching {site.Hostname}…", () => _manager.SaveSiteAsync(site with { RuntimeId = id }, _lifetime.Token));
                picker.SelectedValue = _snapshot.Sites.FirstOrDefault(s => s.Hostname == site.Hostname)?.RuntimeId;
                picker.IsEnabled = true;
            };
            controls.Children.Add(picker);
            var verb = site.Enabled ? "Disable" : "Enable";
            var activation = ActionButton(verb + " site", () => ChangeSiteActivationAsync(site), "SecondaryButtonStyle",
                site.Enabled ? AppIcon.Stop : AppIcon.Play, $"{verb} {site.Hostname}");
            controls.Children.Add(activation);
            row.Children.Add(controls);
            _siteRows.Add(site.Hostname, new(site, status, indicator, detail, activation));
            rows.Children.Add(RowSurface(row));
        }
        _contentHost.Children.Add(Scroll(rows));
    }

    private bool SiteIsServing(WebSite site) => site.Enabled && !Busy && ServiceIsRunning("caddy") && ServiceIsRunning(site.RuntimeId);
    private bool ServiceIsRunning(string id) => _snapshot.Services.Any(service => service.Id == id && service.Status == ServiceStatus.Running);

    private void UpdateSiteStates()
    {
        var caddyRunning = ServiceIsRunning("caddy");
        if (_sitesStartNotice is not null)
            _sitesStartNotice.Visibility = caddyRunning ? Visibility.Collapsed : Visibility.Visible;
        foreach (var row in _siteRows.Values)
        {
            var pendingLabel = _siteActivation is { } change && row.Site.Hostname == change.Hostname ? change.Label : null;
            var serving = caddyRunning && ServiceIsRunning(row.Site.RuntimeId);
            row.Status.Text = pendingLabel ?? (!row.Site.Enabled ? "Disabled" : serving ? "Serving" : "Enabled · Offline");
            var color = pendingLabel is not null || !row.Site.Enabled ? "MutedBrush" : serving ? "SuccessBrush" : "WarningBrush";
            row.Status.SetResourceReference(TextBlock.ForegroundProperty, color);
            row.Indicator.SetResourceReference(Ellipse.FillProperty, color);
            row.Detail.Text = pendingLabel is null && row.Site.Enabled && caddyRunning && !serving ? "The selected PHP runtime is offline." : "";
            row.Detail.Visibility = row.Detail.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private async Task ChangeSiteActivationAsync(WebSite site)
    {
        if (Busy) return;
        var restoreFocus = _siteRows[site.Hostname].Activation.IsKeyboardFocused;
        var operation = site.Enabled ? "Disabling" : "Enabling";
        _siteActivation = (site.Hostname, operation + "…");
        try
        {
            await RunAsync($"{operation} {site.Hostname}…", () => _manager.SaveSiteAsync(site with { Enabled = !site.Enabled }, _lifetime.Token));
        }
        finally
        {
            _siteActivation = null;
            Render(_manager.Snapshot);
            if (restoreFocus && _section == "Sites" && _siteRows.TryGetValue(site.Hostname, out var row))
                Keyboard.Focus(row.Activation);
        }
    }

    private Task RenameSiteAsync(WebSite site)
    {
        var dialog = new Window
        {
            Owner = this, Title = "Rename hostname", Width = 460, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false
        };
        var form = new StackPanel();
        var surface = new Border { Padding = new Thickness(20), Child = form };
        surface.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        dialog.Content = surface;
        form.Children.Add(T($"Rename {site.Hostname}", 20, true));
        form.Children.Add(T("The site URL changes. Its Caddyfile, document root and PHP runtime are kept.", 12, muted: true, margin: new Thickness(0, 8, 0, 12)));
        if (site.Enabled && _snapshot.Services.Any(service =>
            (service.Id == "caddy" || service.Id == site.RuntimeId && service.Kind == ServiceKind.FrankenPhp) && service.Status == ServiceStatus.Running))
            form.Children.Add(T("The affected web services will restart to apply the new hostname.", 12, muted: true, margin: new Thickness(0, 0, 0, 12)));

        var hostname = new TextBox { Text = site.Hostname };
        form.Children.Add(DialogUi.FieldLabel("New hostname", hostname));
        AutomationProperties.SetName(hostname, "New hostname");
        form.Children.Add(hostname);
        var feedback = T("", 12, margin: new Thickness(0, 8, 0, 0));
        AutomationProperties.SetLiveSetting(feedback, AutomationLiveSetting.Polite);
        form.Children.Add(feedback);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = DialogUi.ActionButton("Cancel", "SecondaryButtonStyle");
        cancel.IsCancel = true;
        cancel.Margin = new Thickness(0, 0, 8, 0);
        actions.Children.Add(cancel);
        var save = DialogUi.ActionButton("Rename site", "PrimaryButtonStyle");
        save.IsDefault = true;
        actions.Children.Add(save);
        form.Children.Add(actions);

        var saving = false;
        dialog.Loaded += (_, _) => { hostname.Focus(); hostname.SelectAll(); };
        dialog.Closing += (_, args) => { if (saving && !_shutdown) args.Cancel = true; };
        save.Click += async (_, _) =>
        {
            if (saving) return;
            saving = true;
            hostname.IsEnabled = save.IsEnabled = cancel.IsEnabled = false;
            feedback.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            feedback.Text = "Applying hostname…";
            try
            {
                await _manager.RenameSiteAsync(site.Hostname, hostname.Text, _lifetime.Token);
                saving = false;
                if (dialog.IsVisible) dialog.DialogResult = true;
            }
            catch (Exception error)
            {
                saving = false;
                if (!dialog.IsVisible) return;
                feedback.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
                feedback.Text = error.Message;
                hostname.IsEnabled = save.IsEnabled = cancel.IsEnabled = true;
                hostname.Focus();
            }
        };
        using var hold = HoldFocus();
        dialog.ShowDialog();
        return Task.CompletedTask;
    }

    private Task CreateSiteAsync(ServiceSnapshot[] runtimes)
    {
        using var hold = HoldFocus();
        var dialog = new Window { Owner = this, Title = "Add site", Width = 480,
            SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.SetResourceReference(BackgroundProperty, "SurfaceBrush");
        var form = new StackPanel { Margin = new Thickness(20) };
        dialog.MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 48);
        dialog.Content = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        form.Children.Add(T("Add site", 22, true));
        form.Children.Add(T("Connect a local hostname to a folder and PHP runtime.", 12, muted: true, margin: new Thickness(0, 6, 0, 10)));
        var hostname = new TextBox();
        var root = new TextBox();
        var engine = new ComboBox { ItemsSource = runtimes, DisplayMemberPath = nameof(ServiceSnapshot.DisplayName),
            SelectedValuePath = nameof(ServiceSnapshot.Id), SelectedValue = runtimes.FirstOrDefault()?.Id };
        Field("Hostname", hostname);
        form.Children.Add(T("Use a local hostname, for example app.localhost.", 12, muted: true, margin: new Thickness(0, 5, 0, 0)));
        Field("Document root", root);
        var browse = DialogUi.ActionButton("Browse folders…", "SecondaryButtonStyle", AppIcon.Folder);
        browse.HorizontalAlignment = HorizontalAlignment.Left;
        browse.Margin = new Thickness(0, 8, 0, 0);
        browse.Click += (_, _) =>
        {
            var picker = new OpenFolderDialog { Title = "Choose document root" };
            if (picker.ShowDialog(dialog) == true) root.Text = picker.FolderName;
        };
        form.Children.Add(browse);
        Field("PHP runtime", engine);
        var error = T("", 12);
        error.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite);
        form.Children.Add(error);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = DialogUi.ActionButton("Cancel", "SecondaryButtonStyle"); cancel.IsCancel = true; cancel.Margin = new Thickness(0, 0, 8, 0); actions.Children.Add(cancel);
        var save = DialogUi.ActionButton("Save site", "PrimaryButtonStyle"); save.IsDefault = true; actions.Children.Add(save);
        form.Children.Add(actions);
        dialog.Loaded += (_, _) => hostname.Focus();
        save.Click += async (_, _) =>
        {
            try
            {
                save.IsEnabled = false;
                if (string.IsNullOrWhiteSpace(hostname.Text)) { hostname.Focus(); throw new ArgumentException("Enter a local hostname, for example app.localhost."); }
                if (!Directory.Exists(root.Text) || !Path.IsPathFullyQualified(root.Text)) { root.Focus(); throw new ArgumentException("Choose an existing folder using Browse folders."); }
                if (engine.SelectedValue is not string id) throw new ArgumentException("Select an installed PHP runtime.");
                if (_snapshot.Sites.Any(s => s.Hostname.Equals(hostname.Text.Trim(), StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("A site already uses that hostname.");
                await _manager.SaveSiteAsync(new WebSite(hostname.Text, root.Text, id), _lifetime.Token);
                dialog.DialogResult = true;
            }
            catch (Exception ex) { error.Text = ex.Message; save.IsEnabled = true; }
        };
        dialog.ShowDialog();
        return Task.CompletedTask;

        void Field(string label, Control control)
        {
            var fieldLabel = DialogUi.FieldLabel(label, control); fieldLabel.Margin = new Thickness(0, 12, 0, 6); form.Children.Add(fieldLabel);
            AutomationProperties.SetName(control, label);
            form.Children.Add(control);
        }
    }
}
