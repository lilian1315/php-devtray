using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;
using PhpDevTray.Core;

namespace PhpDevTray.App;

internal sealed partial class ManagerWindow
{
    private void BuildSites()
    {
        Heading("Sites", "Choose a PHP runtime and document root for each local hostname.");
        var runtimes = _snapshot.Services.Where(s => s.Kind is ServiceKind.FrankenPhp or ServiceKind.Php && s.Status != ServiceStatus.NotInstalled).ToArray();
        _pageActions.Children.Add(ActionButton("Add site", () => CreateSiteAsync(runtimes), "PrimaryButtonStyle", AppIcon.Plus,
            enabled: () => !Busy && runtimes.Length > 0));
        var rows = new StackPanel();
        if (runtimes.Length == 0)
            rows.Children.Add(InlineNotice("Install FrankenPHP or PHP NTS to add your first site.", "Install runtimes", () => OpenInstallAsync(ServiceKind.FrankenPhp)));
        if (!HasRuntime(ServiceKind.Caddy))
            rows.Children.Add(InlineNotice("Caddy serves HTTPS for all sites.", "Install Caddy", () => OpenInstallAsync(ServiceKind.Caddy)));
        if (_snapshot.Sites.Count > 0)
            rows.Children.Add(T("Changing the runtime applies immediately. Caddyfile changes update the displayed root automatically. Restart services to apply them.", 12, muted: true, margin: new Thickness(0, 0, 0, 12)));
        foreach (var site in _snapshot.Sites.OrderBy(s => s.Hostname))
        {
            var documentRoot = _snapshot.SiteRoots.GetValueOrDefault(site.Hostname);
            var row = new DockPanel();
            var actions = MoreButton($"{site.Hostname} actions", () => [
                Menu("Edit Caddyfile", () => OpenEditorAsync(Path.Combine(_snapshot.DataDirectory, "user", "caddy", "sites", $"site-{site.Hostname.ToLowerInvariant()}.Caddyfile"))),
                Menu(site.Enabled ? "Disable site" : "Enable site", () => RunAsync("Saving site…", () => _manager.SaveSiteAsync(site with { Enabled = !site.Enabled }))),
                Menu("Open site", () => OpenPathAsync("https://" + site.Hostname), site.Enabled),
                Menu("Open document root", () => OpenPathAsync(documentRoot!), documentRoot is not null)
            ]);
            DockPanel.SetDock(actions, Dock.Right);
            row.Children.Add(actions);
            var labels = new StackPanel();
            labels.Children.Add(T(site.Hostname, 14, true));
            var picker = new ComboBox { ItemsSource = runtimes, DisplayMemberPath = nameof(ServiceSnapshot.DisplayName),
                SelectedValuePath = nameof(ServiceSnapshot.Id), SelectedValue = site.RuntimeId, HorizontalAlignment = HorizontalAlignment.Left,
                MinWidth = 200, Margin = new Thickness(0, 6, 0, 6) };
            AutomationProperties.SetName(picker, $"PHP runtime for {site.Hostname}");
            picker.SelectionChanged += async (_, _) =>
            {
                if (picker.SelectedValue is not string id || id == site.RuntimeId) return;
                picker.IsEnabled = false;
                await RunAsync($"Switching {site.Hostname}…", () => _manager.SaveSiteAsync(site with { RuntimeId = id }, _lifetime.Token));
                picker.SelectedValue = _snapshot.Sites.FirstOrDefault(s => s.Hostname == site.Hostname)?.RuntimeId;
                picker.IsEnabled = true;
            };
            labels.Children.Add(picker);
            if (!site.Enabled) labels.Children.Add(T("Disabled", 12, muted: true));
            if (documentRoot is not null) labels.Children.Add(T(documentRoot, 12, muted: true));
            row.Children.Add(labels);
            rows.Children.Add(RowSurface(row));
        }
        _contentHost.Children.Add(Scroll(rows));
    }

    private Task CreateSiteAsync(ServiceSnapshot[] runtimes)
    {
        using var hold = HoldFocus();
        var dialog = new Window { Owner = this, Title = "Add site", Width = 480,
            SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.SetResourceReference(BackgroundProperty, "SurfaceBrush");
        var form = new StackPanel { Margin = new Thickness(20) };
        dialog.Content = form;
        var hostname = new TextBox();
        var root = new TextBox();
        var engine = new ComboBox { ItemsSource = runtimes, DisplayMemberPath = nameof(ServiceSnapshot.DisplayName),
            SelectedValuePath = nameof(ServiceSnapshot.Id), SelectedValue = runtimes.FirstOrDefault()?.Id };
        Field("Hostname", hostname);
        Field("Document root", root);
        var browse = new Button { Content = "Browse folders…", HorizontalAlignment = HorizontalAlignment.Left };
        browse.Click += (_, _) =>
        {
            var picker = new OpenFolderDialog { Title = "Choose document root" };
            if (picker.ShowDialog(dialog) == true) root.Text = picker.FolderName;
        };
        form.Children.Add(browse);
        Field("PHP runtime", engine);
        var error = T("", 12);
        form.Children.Add(error);
        var save = new Button { Content = "Save site", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        form.Children.Add(save);
        save.Click += async (_, _) =>
        {
            try
            {
                save.IsEnabled = false;
                if (!Directory.Exists(root.Text) || !Path.IsPathFullyQualified(root.Text)) throw new ArgumentException("Choose an existing absolute document root.");
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
            form.Children.Add(T(label, 12, true, margin: new Thickness(0, 10, 0, 4)));
            AutomationProperties.SetName(control, label);
            form.Children.Add(control);
        }
    }
}
