using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace LocalServerManager.App;

/// <summary>Opt-in checks against the actual rendered WPF tree, never run during normal startup.</summary>
internal static class UiSmokeChecks
{
    public static void RequireText(Window window, string expected)
    {
        if (!Descendants(window).Any(child => child is TextBlock block && block.Text.Contains(expected, StringComparison.Ordinal)
            || child is TextBox input && input.Text.Contains(expected, StringComparison.Ordinal)))
            throw new InvalidOperationException($"Expected live page content did not appear: {expected}");
    }

    public static IReadOnlyList<string> Validate(Window window)
    {
        var results = new List<string>();
        foreach (var pair in new[]
        {
            ("TextBrush", "SurfaceBrush"), ("TextBrush", "CardBrush"),
            ("MutedBrush", "CardBrush"), ("MutedBrush", "SurfaceBrush"),
            ("AccentTextBrush", "AccentBrush"), ("ErrorBrush", "CardBrush")
        })
        {
            var foreground = Color(pair.Item1);
            var background = Color(pair.Item2);
            var contrast = Ratio(foreground, background);
            if (contrast < 4.5) throw new InvalidOperationException($"{pair.Item1}/{pair.Item2} contrast is {contrast:0.00}:1; expected at least4.5:1.");
            results.Add($"PASS contrast {pair.Item1}/{pair.Item2}: {contrast:0.00}:1");
        }

        var controls = Descendants(window).OfType<Control>()
            .Where(control => control.IsVisible && control.IsEnabled && control.Focusable && control.IsTabStop)
            .Where(control => control is ButtonBase or TextBox or ComboBox or ListBox)
            .Where(control => !control.Name.StartsWith("PART_", StringComparison.Ordinal)).ToArray();
        foreach (var control in controls)
        {
            var name = AutomationProperties.GetName(control);
            if (string.IsNullOrWhiteSpace(name) && control is ContentControl { Content: string content }) name = content;
            if (string.IsNullOrWhiteSpace(name) && AutomationProperties.GetLabeledBy(control) is Label label) name = label.Content?.ToString();
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException($"An enabled {control.GetType().Name} has no accessible name.");
        }
        results.Add($"PASS accessible names: {controls.Length} enabled keyboard controls");
        var checkedText = 0;
        foreach (var block in Descendants(window).OfType<TextBlock>().Where(block => block.IsVisible && block.IsEnabled && !string.IsNullOrWhiteSpace(block.Text)))
        {
            if (block.Foreground is not SolidColorBrush { Color.A: 255 } foreground) continue;
            var background = FindBackground(block);
            var contrast = Ratio(foreground.Color, background);
            if (contrast < 4.5)
                throw new InvalidOperationException($"Rendered text '{block.Text[..Math.Min(block.Text.Length, 80)]}' has {contrast:0.00}:1 contrast against its surface.");
            checkedText++;
        }
        results.Add($"PASS rendered text contrast: {checkedText} visible enabled text blocks");
        var navigation = controls.OfType<Button>().Where(button => ReferenceEquals(button.Style, window.TryFindResource("NavigationButtonStyle"))).ToArray();
        if (navigation.Length > 0 && navigation.Count(button => Equals(button.Tag, "Selected")) != 1)
            throw new InvalidOperationException("The manager must identify exactly one selected navigation section.");
        if (navigation.FirstOrDefault(button => Equals(button.Tag, "Selected")) is { } selectedNavigation)
            results.Add($"PASS selected navigation: {AutomationProperties.GetName(selectedNavigation)}");
        foreach (var button in navigation)
        {
            if (button.Content is not StackPanel items || !items.Children.OfType<TextBlock>().Any(text => text.IsVisible)) continue;
            if (items.DesiredSize.Width > button.ActualWidth - button.Padding.Left - button.Padding.Right + 1)
                throw new InvalidOperationException($"Navigation label '{AutomationProperties.GetName(button)}' is clipped.");
        }
        results.Add("PASS visible navigation labels fit their buttons");
        foreach (var viewport in Descendants(window).OfType<ScrollViewer>().Where(viewport => viewport.IsVisible && Equals(viewport.Tag, "EmptyStateScroll")).ToArray())
        {
            var lastAction = Descendants(viewport).OfType<Button>().LastOrDefault();
            if (lastAction is null) continue;
            var offset = viewport.VerticalOffset;
            viewport.ScrollToEnd(); window.UpdateLayout();
            var bounds = lastAction.TransformToAncestor(viewport).TransformBounds(new Rect(lastAction.RenderSize));
            if (viewport.ActualHeight < lastAction.ActualHeight || bounds.Top < -1 || bounds.Bottom > viewport.ActualHeight + 1)
                throw new InvalidOperationException("An empty-state action cannot be reached within its scroll viewport.");
            results.Add($"PASS empty-state actions reachable: {AutomationProperties.GetName(viewport)} (scroll {viewport.ScrollableHeight:0} DIPs)");
            viewport.ScrollToVerticalOffset(offset); window.UpdateLayout();
        }
        if (window.ActualWidth <= 0 || window.ActualHeight <= 0) throw new InvalidOperationException("The window has no rendered dimensions.");
        results.Add($"PASS rendered window: {window.ActualWidth:0} × {window.ActualHeight:0} DIPs");
        return results;
    }

    private static Color Color(string key) => (Application.Current.TryFindResource(key) as SolidColorBrush)?.Color
        ?? throw new InvalidOperationException($"Theme resource {key} must resolve to a solid color.");
    private static Color FindBackground(DependencyObject child)
    {
        for (var parent = VisualTreeHelper.GetParent(child); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            // Control.Background need not be painted by its template (CheckBox is one example).
            // Inspect the actual drawing surfaces in the visual tree instead.
            var brush = parent switch { Border border => border.Background, Panel panel => panel.Background, _ => null };
            if (brush is SolidColorBrush { Color.A: 255 } solid) return solid.Color;
        }
        return Color("SurfaceBrush");
    }
    private static double Ratio(Color first, Color second)
    {
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
    private static double Luminance(Color color)
    {
        static double Linear(byte value) { var s = value / 255d; return s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4); }
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
