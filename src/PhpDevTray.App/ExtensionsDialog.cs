using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using PhpDevTray.Core;
using static PhpDevTray.App.DialogUi;

namespace PhpDevTray.App;

internal sealed class ExtensionsDialog : Window
{
    private readonly IEnvironmentManager _manager;
    private readonly string _channel;
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationToken _token;
    private readonly ListBox _list;
    private readonly Grid _columnHeaders;
    private readonly TextBox _search;
    private readonly TextBlock _placeholder;
    private readonly ComboBox _filter;
    private readonly TextBlock _status;
    private readonly TextBlock _counts;
    private readonly TextBlock _error;
    private readonly Border _errorPanel;
    private readonly TextBlock _detailTitle;
    private readonly TextBox _detailText;
    private readonly Border _details;
    private readonly Button _refresh;
    private IReadOnlyList<PhpExtensionInfo> _extensions = [];
    private string? _selectedName;
    private bool _busy, _closed, _loaded, _rendering;

    public ExtensionsDialog(IEnvironmentManager manager, string channel, CancellationToken ownerLifetime)
    {
        _manager = manager;
        _channel = channel;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ownerLifetime);
        _token = _lifetime.Token;
        Title = $"PHP {channel} extensions";
        Width = 680;
        Height = 760;
        MinWidth = 580;
        MinHeight = 460;
        MaxHeight = Math.Max(460, SystemParameters.WorkArea.Height - 48);
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "SurfaceBrush");
        var layout = new Grid();
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            layout.RowDefinitions.Add(new RowDefinition { Height = height });
        Content = layout;

        var header = new Grid { Margin = new Thickness(20, 16, 20, 12) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel();
        title.Children.Add(Text($"PHP {channel}", 11, true, true, new Thickness(0, 0, 0, 4)));
        title.Children.Add(Text("Extensions", 22, true));
        header.Children.Add(title);
        _refresh = ActionButton("Refresh", "SecondaryButtonStyle", AppIcon.Refresh);
        _refresh.VerticalAlignment = VerticalAlignment.Top;
        _refresh.Margin = new Thickness(0, 10, 0, 0);
        AutomationProperties.SetName(_refresh, $"Refresh PHP {channel} extension inventory");
        Grid.SetColumn(_refresh, 1);
        header.Children.Add(_refresh);
        layout.Children.Add(header);

        var tools = new Grid { Margin = new Thickness(20, 0, 20, 10) };
        tools.ColumnDefinitions.Add(new ColumnDefinition());
        tools.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        tools.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(146) });
        _search = new TextBox { Padding = new Thickness(34, 8, 12, 8), MinHeight = 40 };
        AutomationProperties.SetName(_search, "Search extensions");
        AutomationProperties.SetHelpText(_search, "Filter modules by name. Press Control F to search.");
        var searchGroup = new StackPanel();
        searchGroup.Children.Add(FieldLabel("_Search extensions", _search));
        var searchInput = new Grid();
        searchInput.Children.Add(_search);
        var icon = UiIcon.Create(AppIcon.Search, 16, "MutedBrush");
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Margin = new Thickness(11, 0, 0, 0);
        icon.IsHitTestVisible = false;
        searchInput.Children.Add(icon);
        _placeholder = Text("Find a module…", 12, muted: true);
        _placeholder.Margin = new Thickness(35, 0, 10, 0);
        _placeholder.VerticalAlignment = VerticalAlignment.Center;
        _placeholder.IsHitTestVisible = false;
        searchInput.Children.Add(_placeholder);
        searchGroup.Children.Add(searchInput);
        tools.Children.Add(searchGroup);
        _filter = new ComboBox
        {
            ItemsSource = new[] { "All modules", "Enabled", "Disabled", "Built-in", "Needs attention" },
            SelectedIndex = 0, MinHeight = 40
        };
        AutomationProperties.SetName(_filter, "Filter extension state");
        var filterGroup = new StackPanel();
        filterGroup.Children.Add(FieldLabel("_Show", _filter));
        filterGroup.Children.Add(_filter);
        Grid.SetColumn(filterGroup, 2);
        tools.Children.Add(filterGroup);
        Grid.SetRow(tools, 1);
        layout.Children.Add(tools);

        var feedback = new StackPanel { Margin = new Thickness(20, 0, 20, 0) };
        var summary = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        _counts = Text("", 11, muted: true);
        _counts.HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(_counts, Dock.Right);
        summary.Children.Add(_counts);
        _status = Text("Reading modules…", 11, muted: true);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        summary.Children.Add(_status);
        feedback.Children.Add(summary);
        _error = Text("", 12);
        _error.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Polite);
        _errorPanel = new Border
        {
            Child = new ScrollViewer { Content = _error, MaxHeight = 80, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 12), Visibility = Visibility.Collapsed
        };
        _errorPanel.SetResourceReference(Border.BorderBrushProperty, "ErrorBrush");
        _errorPanel.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        feedback.Children.Add(_errorPanel);
        Grid.SetRow(feedback, 2);
        layout.Children.Add(feedback);

        var body = new Grid { Margin = new Thickness(20, 0, 20, 10) };
        body.RowDefinitions.Add(new RowDefinition());
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var table = new Grid();
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        table.RowDefinitions.Add(new RowDefinition());
        var columns = _columnHeaders = RowGrid();
        columns.Margin = new Thickness(14, 0, 0, 0);
        columns.HorizontalAlignment = HorizontalAlignment.Left;
        columns.Height = 28;
        Cell(columns, Text("Extension", 11, true, true), 0);
        Cell(columns, Text("Origin", 11, true, true), 1);
        Cell(columns, Text("State", 11, true, true), 2);
        Cell(columns, Text("Enabled", 11, true, true), 3, true);
        var tableHeader = new Border { Child = columns, BorderThickness = new Thickness(0, 0, 0, 1) };
        tableHeader.SetResourceReference(Border.BorderBrushProperty, "DividerBrush");
        tableHeader.SetResourceReference(Border.BackgroundProperty, "SidebarBrush");
        table.Children.Add(tableHeader);
        _list = new ListBox { BorderThickness = new Thickness(0), Padding = new Thickness(4), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        _list.SetResourceReference(BackgroundProperty, "CardBrush");
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(_list, ScrollBarVisibility.Auto);
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        AutomationProperties.SetName(_list, $"PHP {channel} extension inventory");
        Grid.SetRow(_list, 1);
        table.Children.Add(_list);
        var tableBorder = new Border { Child = table, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), ClipToBounds = true };
        tableBorder.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        tableBorder.SetResourceReference(Border.BorderBrushProperty, "DividerBrush");
        body.Children.Add(tableBorder);

        _detailTitle = Text("Module details", 12, true);
        _detailText = new TextBox
        {
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0),
            MinHeight = 0, MaxHeight = 64, FontSize = 12, Margin = new Thickness(0, 3, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        _detailText.SetResourceReference(ForegroundProperty, "MutedBrush");
        AutomationProperties.SetName(_detailText, "Selected extension details");
        var detailContent = new StackPanel();
        detailContent.Children.Add(_detailTitle);
        detailContent.Children.Add(_detailText);
        _details = new Border { Child = detailContent, Padding = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        Grid.SetRow(_details, 1);
        body.Children.Add(_details);
        Grid.SetRow(body, 3);
        layout.Children.Add(body);

        var footer = new Grid { Margin = new Thickness(20, 10, 20, 10) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var note = Text("Changes apply immediately. Running PHP restarts.", 11, muted: true);
        note.VerticalAlignment = VerticalAlignment.Center;
        note.Margin = new Thickness(0, 0, 18, 0);
        footer.Children.Add(note);
        var close = ActionButton("Close", "SecondaryButtonStyle");
        close.IsCancel = true;
        close.MinWidth = 86;
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 1);
        footer.Children.Add(close);
        var footerBorder = new Border { Child = footer, BorderThickness = new Thickness(0, 1, 0, 0) };
        footerBorder.SetResourceReference(Border.BorderBrushProperty, "DividerBrush");
        footerBorder.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        Grid.SetRow(footerBorder, 4);
        layout.Children.Add(footerBorder);

        _search.TextChanged += (_, _) => RenderInventory();
        _filter.SelectionChanged += (_, _) => RenderInventory();
        _refresh.Click += async (_, _) => await RefreshAsync();
        _list.SelectionChanged += (_, _) =>
        {
            if (_rendering || _list.SelectedItem is not ListBoxItem { Tag: PhpExtensionInfo extension }) return;
            _selectedName = extension.Name;
            ShowDetails(extension);
        };
        Loaded += async (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            _search.Focus();
            await RefreshAsync();
        };
        Closing += OnClosing;
        Closed += (_, _) => _lifetime.Dispose();
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape) { Close(); args.Handled = true; }
            else if (args.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                _search.Focus();
                _search.SelectAll();
                args.Handled = true;
            }
        };
    }

    internal Task LoadForSmokeAsync() { _loaded = true; return RefreshAsync(); }

    private async Task RefreshAsync()
    {
        if (_busy || _closed) return;
        await RunAsync("Reading modules…", async () =>
        {
            var inventory = await _manager.GetPhpExtensionsAsync(_channel, _token);
            if (!_closed) _extensions = inventory;
        });
    }

    private async Task ChangeAsync(PhpExtensionInfo extension, bool enabled)
    {
        if (_busy || _closed || !CanToggle(extension)) return;
        _selectedName = extension.Name;
        await RunAsync($"{(enabled ? "Enabling" : "Disabling")} {extension.Name}…", async () =>
        {
            await _manager.SetPhpExtensionEnabledAsync(_channel, extension.Name, enabled, _token);
            var inventory = await _manager.GetPhpExtensionsAsync(_channel, _token);
            if (!_closed) _extensions = inventory;
        });
    }

    private async Task RunAsync(string message, Func<Task> action)
    {
        _busy = true;
        _error.Text = "";
        _errorPanel.Visibility = Visibility.Collapsed;
        _status.Text = message;
        RenderInventory();
        try { await action(); }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (_closed) return;
            _error.Text = exception.Message;
            _errorPanel.Visibility = Visibility.Visible;
        }
        finally { _busy = false; if (!_closed) RenderInventory(); }
    }

    private void RenderInventory()
    {
        if (_closed) return;
        _refresh.IsEnabled = !_busy;
        _placeholder.Visibility = string.IsNullOrEmpty(_search.Text) ? Visibility.Visible : Visibility.Collapsed;
        var search = _search.Text.Trim();
        var visible = _extensions.Where(extension => extension.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Where(extension => _filter.SelectedIndex switch
            {
                1 => extension.IsEnabled,
                2 => !extension.IsBuiltIn && !extension.IsEnabled,
                3 => extension.IsBuiltIn,
                4 => !string.IsNullOrWhiteSpace(extension.Error),
                _ => true
            }).OrderBy(extension => extension.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        if (!_busy) _status.Text = $"{visible.Length} of {_extensions.Count} modules";
        _counts.Text = _extensions.Count == 0 ? "" : $"{_extensions.Count(extension => extension.IsEnabled)} loaded · {_extensions.Count(extension => extension.IsBuiltIn)} built-in";
        _rendering = true;
        try
        {
            _list.Items.Clear();
            if (visible.Length == 0)
            {
                var message = _busy ? "Reading PHP's module inventory…"
                    : _extensions.Count == 0 ? "No module inventory is available. Refresh to try again."
                    : "No modules match these filters.";
                _list.Items.Add(new ListBoxItem { Content = Text(message, 12, muted: true), IsEnabled = false, Padding = new Thickness(14, 22, 14, 22), Focusable = false });
                _details.Visibility = Visibility.Collapsed;
                return;
            }
            foreach (var group in new[] { (Title: "Bundled & configured", Values: visible.Where(extension => !extension.IsBuiltIn)),
                         (Title: "Built-in modules", Values: visible.Where(extension => extension.IsBuiltIn)) })
            {
                var values = group.Values.ToArray();
                if (values.Length == 0) continue;
                _list.Items.Add(new ListBoxItem { Content = Text($"{group.Title}  ·  {values.Length}", 10, true, true), IsEnabled = false, Focusable = false, Padding = new Thickness(10, 4, 10, 4), MinHeight = 24 });
                foreach (var extension in values) _list.Items.Add(ExtensionRow(extension));
            }
            var firstModule = _list.Items.OfType<ListBoxItem>().First(item => item.Tag is PhpExtensionInfo);
            // Match the measured row width, including the themed scrollbar's actual gutter.
            // Fixed guesses drift when the window or scrollbar style changes.
            _columnHeaders.SetBinding(WidthProperty, new Binding(nameof(ActualWidth)) { Source = firstModule.Content });
            var selected = _list.Items.OfType<ListBoxItem>().FirstOrDefault(item => item.Tag is PhpExtensionInfo extension && extension.Name == _selectedName)
                ?? firstModule;
            _list.SelectedItem = selected;
            if (selected.Tag is PhpExtensionInfo current) { _selectedName = current.Name; ShowDetails(current); }
        }
        finally { _rendering = false; }
    }

    private ListBoxItem ExtensionRow(PhpExtensionInfo extension)
    {
        var row = RowGrid();
        row.MinHeight = 40;
        var nameGroup = new DockPanel { LastChildFill = true };
        if (!string.IsNullOrWhiteSpace(extension.Error))
        {
            var warning = UiIcon.Create(AppIcon.Info, 14, "WarningBrush");
            warning.Margin = new Thickness(0, 0, 7, 0);
            warning.VerticalAlignment = VerticalAlignment.Center;
            warning.ToolTip = "Configuration details available below";
            nameGroup.Children.Add(warning);
        }
        var name = Text(extension.Name, 12, true);
        name.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.TextWrapping = TextWrapping.NoWrap;
        name.VerticalAlignment = VerticalAlignment.Center;
        nameGroup.Children.Add(name);
        Cell(row, nameGroup, 0);
        var external = !extension.IsBuiltIn && extension.IsEnabled && !extension.IsManaged;
        Cell(row, Text(extension.IsBuiltIn ? "Built-in" : external ? "External" : "Bundled", 11, muted: true), 1);
        var state = extension.IsEnabled ? "Loaded" : extension.IsBuiltIn ? "Not verified" : extension.IsAvailable ? "Disabled" : "Missing DLL";
        var stateText = Text(state, 11);
        stateText.SetResourceReference(TextBlock.ForegroundProperty, extension.IsEnabled ? "SuccessBrush" : !extension.IsAvailable ? "WarningBrush" : "MutedBrush");
        Cell(row, stateText, 2);
        if (extension.IsBuiltIn || external)
        {
            var icon = UiIcon.Create(external ? AppIcon.ExternalLink : AppIcon.Check, 15, "MutedBrush");
            icon.ToolTip = external ? "Configured by a user-owned loading directive" : "Built into PHP; cannot be toggled";
            AutomationProperties.SetName(icon, external ? "Configured externally" : "Built-in module, no toggle");
            Cell(row, icon, 3, true);
        }
        else if (extension.IsManaged && !extension.IsEnabled)
        {
            var remove = ActionButton("Remove", "QuietButtonStyle");
            remove.Padding = new Thickness(5, 4, 5, 4);
            remove.MinWidth = 0;
            remove.FontSize = 11;
            remove.IsEnabled = !_busy;
            remove.ToolTip = "Remove the loading directive for this failed extension";
            AutomationProperties.SetName(remove, $"Remove failed PHP {_channel} {extension.Name} loading directive");
            remove.Click += async (_, _) => await ChangeAsync(extension, false);
            Cell(row, remove, 3, true);
        }
        else
        {
            var toggle = new CheckBox
            {
                IsChecked = extension.IsEnabled, IsEnabled = !_busy && CanToggle(extension),
                MinWidth = 40, MinHeight = 40, HorizontalContentAlignment = HorizontalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                ToolTip = extension.IsEnabled ? "Disable extension" : extension.IsAvailable ? "Enable extension" : "The extension DLL is not available"
            };
            AutomationProperties.SetName(toggle, $"{(extension.IsEnabled ? "Disable" : "Enable")} PHP {_channel} {extension.Name}");
            toggle.Click += async (_, _) => { toggle.IsChecked = extension.IsEnabled; await ChangeAsync(extension, !extension.IsEnabled); };
            Cell(row, toggle, 3, true);
        }
        var item = new ListBoxItem { Content = row, Tag = extension, Padding = new Thickness(10, 0, 10, 0), HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0) };
        AutomationProperties.SetName(item, $"{extension.Name}, {state}, {(extension.IsBuiltIn ? "built-in" : external ? "configured externally" : "bundled extension")}");
        return item;
    }

    private void ShowDetails(PhpExtensionInfo extension)
    {
        _details.Visibility = Visibility.Visible;
        _detailTitle.Text = "Module details";
        var description = extension.IsBuiltIn ? "Included in PHP itself. Built-in modules cannot be disabled individually."
            : extension.IsEnabled && !extension.IsManaged ? "Loaded by a user-owned configuration directive. Edit its source before managing it here."
            : extension.IsEnabled ? "Loaded from the managed configuration for this PHP channel."
            : extension.IsManaged ? "A loading directive is configured, but PHP did not load this module. Repair its dependency or remove the directive."
            : extension.IsAvailable ? "Bundled DLL available. Enable it to load this module for the channel."
            : "The extension DLL is missing from this installation.";
        _detailText.Text = extension.Name + "\n" + (string.IsNullOrWhiteSpace(extension.Error) ? description : description + "\n\n" + extension.Error);
        _detailText.SetResourceReference(ForegroundProperty, string.IsNullOrWhiteSpace(extension.Error) ? "MutedBrush" : "WarningBrush");
    }

    private static bool CanToggle(PhpExtensionInfo extension) => !extension.IsBuiltIn && (extension.IsAvailable || extension.IsManaged) && (!extension.IsEnabled || extension.IsManaged);
    private void OnClosing(object? sender, CancelEventArgs args) { _closed = true; _lifetime.Cancel(); }
    private static Grid RowGrid()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        return grid;
    }
    private static void Cell(Grid row, FrameworkElement element, int column, bool centered = false)
    {
        element.VerticalAlignment = VerticalAlignment.Center;
        if (centered) element.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetColumn(element, column);
        row.Children.Add(element);
    }
}

internal static class DialogUi
{
    internal static Label FieldLabel(string content, Control target)
    {
        var label = new Label { Content = content, Target = target, Padding = new Thickness(0), Margin = new Thickness(0, 0, 0, 6), FontSize = 11, FontWeight = FontWeights.SemiBold };
        label.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        return label;
    }
    internal static Button ActionButton(string content, string styleKey, AppIcon? icon = null)
    {
        var button = new Button { Margin = new Thickness(0) };
        button.SetResourceReference(FrameworkElement.StyleProperty, styleKey);
        if (icon is null) button.Content = content;
        else
        {
            var children = new StackPanel { Orientation = Orientation.Horizontal };
            var image = UiIcon.Create(icon.Value, 15);
            image.Margin = new Thickness(0, 0, 7, 0);
            image.VerticalAlignment = VerticalAlignment.Center;
            children.Children.Add(image);
            children.Children.Add(new TextBlock { Text = content, VerticalAlignment = VerticalAlignment.Center });
            button.Content = children;
        }
        return button;
    }
    internal static TextBlock Text(string text, double size, bool bold = false, bool muted = false, Thickness? margin = null)
    {
        var value = new TextBlock { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = margin ?? new Thickness(0) };
        value.SetResourceReference(TextBlock.ForegroundProperty, muted ? "MutedBrush" : "TextBrush");
        return value;
    }
}
