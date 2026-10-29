using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using LocalServerManager.Core;
using LocalServerManager.Infrastructure.Configuration;
using Microsoft.Win32;
using static LocalServerManager.App.DialogUi;

namespace LocalServerManager.App;

internal sealed class SiteDialog : Window
{
    private readonly TextBox _hostname;
    private readonly TextBox _documentRoot;
    private readonly ComboBox _php;
    private readonly TextBox _port;
    private readonly TextBlock _address;
    private readonly Button _create;
    private readonly Dictionary<Control, FieldFeedback> _feedback = [];

    public SiteDefinition? Site { get; private set; }

    public SiteDialog(IReadOnlyList<string> channels)
    {
        Title = "Create local site";
        Width = 560;
        MinWidth = 500;
        MaxHeight = Math.Max(420, SystemParameters.WorkArea.Height - 48);
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SetResourceReference(BackgroundProperty, "SurfaceBrush");
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = layout;

        var header = new StackPanel { Margin = new Thickness(26, 24, 26, 22) };
        header.Children.Add(Text("Create a local site", 24, true));
        header.Children.Add(Text("Connect a project folder to Nginx and PHP.", 13, muted: true, margin: new Thickness(0, 7, 0, 0)));
        layout.Children.Add(header);
        var form = new StackPanel { Margin = new Thickness(26, 0, 26, 22) };
        _hostname = new TextBox { Text = "app.test", MinHeight = 40 };
        form.Children.Add(Field("_Hostname", _hostname, "A local name such as app.test. Use letters, numbers, dots, and hyphens."));

        _documentRoot = new TextBox { MinHeight = 40 };
        var rootInput = new Grid();
        rootInput.ColumnDefinitions.Add(new ColumnDefinition());
        rootInput.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        rootInput.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rootInput.Children.Add(_documentRoot);
        var browse = ActionButton("Browse…", "SecondaryButtonStyle", AppIcon.Folder);
        browse.Padding = new Thickness(10, 7, 12, 7);
        browse.MinHeight = 40;
        AutomationProperties.SetName(browse, "Browse for the site document root");
        browse.Click += (_, _) =>
        {
            var picker = new OpenFolderDialog { Title = "Select the public document root" };
            if (Directory.Exists(_documentRoot.Text)) picker.InitialDirectory = _documentRoot.Text;
            if (picker.ShowDialog(this) != true) return;
            _documentRoot.Text = picker.FolderName;
            _documentRoot.Focus();
            _documentRoot.CaretIndex = _documentRoot.Text.Length;
        };
        Grid.SetColumn(browse, 2);
        rootInput.Children.Add(browse);
        form.Children.Add(Field("_Document root", _documentRoot, "Choose the public web folder, often your project's public directory.", rootInput));

        var runtime = new Grid();
        runtime.ColumnDefinitions.Add(new ColumnDefinition());
        runtime.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        runtime.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        _php = new ComboBox { ItemsSource = channels.Distinct().ToArray(), MinHeight = 40, SelectedIndex = channels.Count > 0 ? 0 : -1 };
        runtime.Children.Add(Field("_PHP channel", _php, "Used only by this site."));
        _port = new TextBox { Text = "8080", MinHeight = 40 };
        var portField = Field("HTTP _port", _port, "Default: 8080.");
        Grid.SetColumn(portField, 2);
        runtime.Children.Add(portField);
        form.Children.Add(runtime);

        var preview = new Grid();
        preview.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        preview.ColumnDefinitions.Add(new ColumnDefinition());
        var globe = UiIcon.Create(AppIcon.Globe, 17, "AccentBrush");
        globe.VerticalAlignment = VerticalAlignment.Top;
        globe.Margin = new Thickness(0, 2, 0, 0);
        preview.Children.Add(globe);
        var previewContent = new StackPanel();
        previewContent.Children.Add(Text("Site address", 10, true, true));
        _address = Text("", 13, true, margin: new Thickness(0, 5, 0, 0));
        _address.FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas");
        AutomationProperties.SetName(_address, "Preview of the site address");
        previewContent.Children.Add(_address);
        previewContent.Children.Add(Text("Point this hostname to 127.0.0.1 in your hosts file or local DNS.", 11, muted: true, margin: new Thickness(0, 8, 0, 0)));
        Grid.SetColumn(previewContent, 1);
        preview.Children.Add(previewContent);
        var previewPanel = new Border { Child = preview, CornerRadius = new CornerRadius(9), Padding = new Thickness(14), Margin = new Thickness(0, 2, 0, 0) };
        previewPanel.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
        form.Children.Add(previewPanel);
        var scroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1);
        layout.Children.Add(scroll);

        var footer = new Grid { Margin = new Thickness(26, 15, 26, 17) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var localOnly = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var monitor = UiIcon.Create(AppIcon.Monitor, 14, "MutedBrush");
        monitor.Margin = new Thickness(0, 0, 7, 0);
        localOnly.Children.Add(monitor);
        localOnly.Children.Add(Text("Loopback only", 11, muted: true));
        footer.Children.Add(localOnly);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var cancel = ActionButton("Cancel", "SecondaryButtonStyle");
        cancel.IsCancel = true;
        cancel.Margin = new Thickness(0, 0, 8, 0);
        cancel.Click += (_, _) => DialogResult = false;
        _create = ActionButton("Create site", "PrimaryButtonStyle", AppIcon.Plus);
        _create.IsDefault = true;
        _create.Click += (_, _) => CreateSite();
        AutomationProperties.SetName(_create, "Create local site");
        actions.Children.Add(cancel);
        actions.Children.Add(_create);
        Grid.SetColumn(actions, 1);
        footer.Children.Add(actions);
        var footerBorder = new Border { Child = footer, BorderThickness = new Thickness(0, 1, 0, 0) };
        footerBorder.SetResourceReference(Border.BorderBrushProperty, "DividerBrush");
        footerBorder.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        Grid.SetRow(footerBorder, 2);
        layout.Children.Add(footerBorder);

        _hostname.TextChanged += (_, _) => { ClearError(_hostname); UpdateAddress(); };
        _documentRoot.TextChanged += (_, _) => { ClearError(_documentRoot); _documentRoot.ToolTip = _documentRoot.Text; };
        _port.TextChanged += (_, _) => { ClearError(_port); UpdateAddress(); };
        _php.SelectionChanged += (_, _) => ClearError(_php);
        if (channels.Count == 0)
        {
            _create.IsEnabled = false;
            SetError(_php, "Install a PHP channel before creating a PHP site.");
        }
        Loaded += (_, _) => { _hostname.Focus(); _hostname.SelectAll(); };
        UpdateAddress();
    }

    private FrameworkElement Field(string labelText, Control input, string helpText, FrameworkElement? inputLayout = null)
    {
        var container = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        var label = FieldLabel(labelText, input);
        label.FontSize = 12;
        label.Margin = new Thickness(0, 0, 0, 7);
        AutomationProperties.SetName(input, labelText.Replace("_", ""));
        AutomationProperties.SetHelpText(input, helpText);
        container.Children.Add(label);
        container.Children.Add(inputLayout ?? input);
        var hint = Text(helpText, 11, muted: true, margin: new Thickness(0, 6, 0, 0));
        container.Children.Add(hint);
        var error = Text("", 11, margin: new Thickness(0, 6, 0, 0));
        error.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
        error.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite);
        container.Children.Add(error);
        _feedback[input] = new(error, hint, helpText);
        return container;
    }

    private void CreateSite()
    {
        foreach (var input in _feedback.Keys) ClearError(input);
        Control? firstError = null;
        var host = _hostname.Text.Trim().ToLowerInvariant();
        string root = "";
        var number = 0;
        try { _ = RuntimePaths.SafeHostname(host); }
        catch (ArgumentException) { Invalid(_hostname, "Enter a hostname such as app.test, without a scheme, port, or path."); }
        if (!Path.IsPathFullyQualified(_documentRoot.Text.Trim()) || !Directory.Exists(_documentRoot.Text.Trim()))
            Invalid(_documentRoot, "Choose an existing folder using its full path.");
        else
        {
            try { root = Path.GetFullPath(_documentRoot.Text.Trim()); _ = RuntimePaths.NginxQuote(root); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            { Invalid(_documentRoot, "This folder path contains characters Nginx cannot use. Choose another folder."); }
        }
        if (_php.SelectedItem is not string { Length: > 0 }) Invalid(_php, "Choose an installed PHP channel.");
        if (!int.TryParse(_port.Text.Trim(), out number) || number is < 1 or > 65535)
            Invalid(_port, "Enter a port from 1 to 65535.");
        else if (number == 8080 && host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            Invalid(_hostname, "localhost:8080 is reserved by the manager. Use a site name such as app.test.");
        if (firstError is not null)
        {
            firstError.Focus();
            firstError.BringIntoView();
            if (firstError is TextBox text) text.SelectAll();
            return;
        }
        var identifier = Regex.Replace(host, "[^a-z0-9-]", "-");
        if (identifier.Length > 64)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(host))).ToLowerInvariant()[..8];
            identifier = identifier[..55].TrimEnd('-') + "-" + hash;
        }
        Site = new SiteDefinition { Id = identifier, Hostname = host, DocumentRoot = root, PhpChannel = (string)_php.SelectedItem, Port = number };
        DialogResult = true;
        void Invalid(Control input, string message) { SetError(input, message); firstError ??= input; }
    }

    private void SetError(Control control, string message)
    {
        var feedback = _feedback[control];
        feedback.Error.Text = message;
        feedback.Error.Visibility = Visibility.Visible;
        feedback.Hint.Visibility = Visibility.Collapsed;
        control.SetResourceReference(Control.BorderBrushProperty, "ErrorBrush");
        AutomationProperties.SetHelpText(control, message);
    }

    private void ClearError(Control control)
    {
        if (!_feedback.TryGetValue(control, out var feedback)) return;
        feedback.Error.Text = "";
        feedback.Error.Visibility = Visibility.Collapsed;
        feedback.Hint.Visibility = Visibility.Visible;
        control.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");
        AutomationProperties.SetHelpText(control, feedback.HelpText);
    }

    private void UpdateAddress()
    {
        var host = _hostname.Text.Trim().ToLowerInvariant();
        if (host.Length > 0 && int.TryParse(_port.Text.Trim(), out var port) && port is >= 1 and <= 65535)
            _address.Text = $"http://{host}{(port == 80 ? "" : ":" + port)}/";
        else _address.Text = "Enter a hostname and port to preview the address.";
    }

    private sealed record FieldFeedback(TextBlock Error, TextBlock Hint, string HelpText);
}
