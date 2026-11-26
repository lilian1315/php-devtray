using Button = Wpf.Ui.Controls.Button;
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
    private sealed record SiteRow(TextBlock Status, TextBlock Root, ComboBox Picker, Button Activation);
    private readonly Dictionary<string, SiteRow> _siteRows = new(StringComparer.OrdinalIgnoreCase);
    private Button? _siteServerControl;
    private (string Hostname, string Label)? _siteActivation;
    private WebSite CurrentSite(string hostname) => _snapshot.Sites.Single(site => site.Hostname == hostname);

    private void BuildSites()
    {
        Heading("Sites", "Local hostnames, folders and PHP engines.");
        var runtimes = _snapshot.Services.Where(service => service.Kind is ServiceKind.FrankenPhp or ServiceKind.Php && service.Status != ServiceStatus.NotInstalled).ToArray();
        _pageActions.Children.Add(ActionButton("Add site", () => CreateSiteAsync(runtimes), "PrimaryButtonStyle", AppIcon.Plus, enabled: () => !Busy && runtimes.Length > 0));
        _siteServerControl = ActionButton("Start Caddy", () => ToggleServiceAsync(ServiceKind.Caddy), enabled: () => !Busy && HasRuntime(ServiceKind.Caddy) && (ServiceIsRunning("caddy") || _snapshot.Sites.Any(site => site.Enabled)));
        _pageActions.Children.Add(_siteServerControl);
        _pageActions.Children.Add(RestartButton(() => RestartServiceAsync("caddy", "Caddy"), () => ServiceIsRunning("caddy"), "Restart Caddy"));
        var rows = new StackPanel();
        if (runtimes.Length == 0) rows.Children.Add(InlineNotice("Install PHP to add your first site.", "Install PHP", () => OpenInstallAsync(ServiceKind.Php)));
        if (!HasRuntime(ServiceKind.Caddy)) rows.Children.Add(InlineNotice("Caddy serves HTTPS for all sites.", "Install Caddy", () => OpenInstallAsync(ServiceKind.Caddy)));
        foreach (var site in _snapshot.Sites.OrderBy(site => site.Hostname))
        {
            var host = site.Hostname;
            var labels = new StackPanel(); labels.Children.Add(T(host, 16, true));
            var root = RuntimeMetadata(""); root.MinHeight = 20; root.TextWrapping = TextWrapping.NoWrap; root.TextTrimming = TextTrimming.CharacterEllipsis; labels.Children.Add(root);
            var status = RuntimeMetadata(""); status.MinHeight = 20; labels.Children.Add(status);
            var controls = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            var picker = new ComboBox { ItemsSource = runtimes, DisplayMemberPath = nameof(ServiceSnapshot.DisplayName), SelectedValuePath = nameof(ServiceSnapshot.Id), SelectedValue = site.RuntimeId,
                Width = 184, MinHeight = 32, Margin = new Thickness(0, 0, 8, 0) };
            AutomationProperties.SetName(picker, $"PHP runtime for {host}");
            picker.SelectionChanged += async (_, _) =>
            {
                if (_updatingInputs || picker.SelectedValue is not string id || id == CurrentSite(host).RuntimeId) return;
                await RunAsync($"Switching {host}…", () => _manager.SaveSiteAsync(CurrentSite(host) with { RuntimeId = id }, _lifetime.Token));
                UpdateSiteStates();
            };
            picker.Margin = new Thickness(0, 12, 0, 0); picker.HorizontalAlignment = HorizontalAlignment.Left;
            labels.Children.Add(picker);
            var activation = ActionButton("Disable site", () => ChangeSiteActivationAsync(CurrentSite(host)));
            controls.Children.Add(activation);
            controls.Children.Add(MoreButton(host + " actions", () => [
                Menu("Rename hostname…", () => RenameSiteAsync(CurrentSite(host)), !Busy),
                Menu("Edit Caddyfile", () => OpenEditorAsync(Path.Combine(_snapshot.DataDirectory, "user", "caddy", "sites", CurrentSite(host).CaddyfileName))),
                Menu("Open site", () => OpenPathAsync("https://" + host), SiteIsServing(CurrentSite(host))),
                Menu("Open document root", () => OpenPathAsync(_snapshot.SiteRoots[host]!), _snapshot.SiteRoots.GetValueOrDefault(host) is not null)
            ]));
            _siteRows[host] = new(status, root, picker, activation);
            rows.Children.Add(RuntimeRow(labels, controls));
        }
        _contentHost.Children.Add(Scroll(rows));
    }
    private bool SiteIsServing(WebSite site) => site.Enabled && !Busy && ServiceIsRunning("caddy") && ServiceIsRunning(site.RuntimeId);
    private bool ServiceIsRunning(string id) => _snapshot.Services.Any(service => service.Id == id && service.Status == ServiceStatus.Running);
    private void UpdateSiteStates()
    {
        var caddyRunning = ServiceIsRunning("caddy");
        if (_siteServerControl is not null)
        {
            _siteServerControl.Content = caddyRunning ? "Stop Caddy" : "Start Caddy";
            AutomationProperties.SetName(_siteServerControl, caddyRunning ? "Stop Caddy" : "Start Caddy");
        }
        _updatingInputs = true;
        try
        {
            foreach (var (host, row) in _siteRows)
            {
                var site = _snapshot.Sites.FirstOrDefault(site => site.Hostname == host); if (site is null) continue;
                var pending = _siteActivation is { } change && host == change.Hostname ? change.Label : null;
                var serving = caddyRunning && ServiceIsRunning(site.RuntimeId);
                row.Status.Text = pending ?? (!site.Enabled ? "Disabled" : serving ? "Serving" : "Enabled · Offline");
                row.Status.SetResourceReference(TextBlock.ForegroundProperty, pending is not null || !site.Enabled ? "MutedBrush" : serving ? "SuccessBrush" : "WarningBrush");
                row.Root.Text = _snapshot.SiteRoots.GetValueOrDefault(host) ?? "Document root unavailable"; row.Root.ToolTip = row.Root.Text;
                row.Status.ToolTip = site.Enabled && caddyRunning && !serving ? "The selected PHP runtime is offline." : row.Status.Text;
                row.Picker.SelectedValue = site.RuntimeId; row.Picker.IsEnabled = !Busy;
                row.Activation.Content = site.Enabled ? "Disable site" : "Enable site";
                AutomationProperties.SetName(row.Activation, $"{(site.Enabled ? "Disable" : "Enable")} {host}");
            }
        }
        finally { _updatingInputs = false; }
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
        var dialog = new FluentDialogWindow
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
        var dialog = new FluentDialogWindow { Owner = this, Title = "Add site", Width = 480,
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
