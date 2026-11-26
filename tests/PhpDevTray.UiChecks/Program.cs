using Button = Wpf.Ui.Controls.Button;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using PhpDevTray.App;
using PhpDevTray.Core;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            var app = new App { IsResourceCheckHost = true };
            app.InitializeComponent();
            if (args.Contains("--desktop-preview")) return DesktopPreview.Run(app);
            if (args.Length == 2 && args[0] == "--desktop-capture") return DesktopPreview.Run(app, args[1]);
            var themeType = typeof(App).Assembly.GetType("PhpDevTray.App.ThemeManager", true)!;
            using var theme = (IDisposable)Activator.CreateInstance(themeType, nonPublic: true)!;
            var preference = themeType.GetMethod("SetPreference")!;
            void SetTheme(ThemePreference value) => preference.Invoke(theme, [value]);
            SetTheme(ThemePreference.Light);
            PhpDevTray.UiChecks.AppIconChecks.Run();
            var lightBrush = app.Resources["CardBrush"];
            SetTheme(ThemePreference.Light);
            Require(ReferenceEquals(lightBrush, app.Resources["CardBrush"]), "Repeated theme application must reuse cached brushes.");
            CheckContrast(app.Resources);
            SetTheme(ThemePreference.Dark);
            CheckContrast(app.Resources);
            SetTheme(ThemePreference.Light);
            Require(ReferenceEquals(lightBrush, app.Resources["CardBrush"]), "Light palette brush instances must be cached across switches.");

            foreach (var icon in Enum.GetValues<AppIcon>()) _ = UiIcon.Create(icon);
            var inheritedIcon = UiIcon.Create(AppIcon.Play);
            var iconButton = new Button { Content = inheritedIcon, Foreground = Brushes.OrangeRed, Style = (Style)app.FindResource("PrimaryButtonStyle") };
            Layout(iconButton, 120, 40);
            Require(TextElement.GetForeground(inheritedIcon) == Brushes.OrangeRed, "Icon stroke must inherit its owning button's foreground.");
            var button = new Button { Content = "Download", Style = (Style)app.FindResource("PrimaryButtonStyle") };
            Layout(button, 140, 40);
            var enabledSize = button.DesiredSize;
            button.IsEnabled = false;
            Layout(button, 140, 40);
            Require(!button.IsEnabled && button.Template is not null, "WPF UI must retain the disabled button template.");
            Require(button.DesiredSize == enabledSize, "Interaction states must not shift control geometry.");

            var input = new TextBox { Text = "native text selection" };
            Layout(input, 220, 40);
            Require(input.Template.FindName("PART_ContentHost", input) is ScrollViewer, "TextBox must retain its native content host.");
            input.Select(0, 6);
            Require(input.SelectionLength == 6 && input.SelectedText == "native", "Styled text input must retain native selection semantics.");
            var originalTextStart = input.GetRectFromCharacterIndex(0);
            input.Padding = new Thickness(34, 8, 12, 8);
            Layout(input, 300, 40);
            var textStart = input.GetRectFromCharacterIndex(0);
            Require(!textStart.IsEmpty && Math.Abs(textStart.Left - originalTextStart.Left - 24) < 1,
                "Search inputs must apply the extra icon padding once.");

            var combo = new ComboBox { ItemsSource = new[] { "PHP 8.4", "PHP 8.5" }, SelectedIndex = 0 };
            Layout(combo, 200, 40);
            Require(VisualChildren<Popup>(combo).Any(), "ComboBox must retain its dropdown popup.");
            Require(combo.Template.FindName("PART_EditableTextBox", combo) is TextBox, "ComboBox must retain its editable text part.");
            combo.SelectedIndex = 1;
            Require((string)combo.SelectedItem == "PHP 8.5", "ComboBox must retain native selection semantics.");
            var dropdownToggle = VisualChildren<ToggleButton>(combo).First();
            Require(BindingOperations.GetBindingExpression(dropdownToggle, ToggleButton.IsCheckedProperty)?.ParentBinding.Mode == BindingMode.TwoWay,
                "The dropdown toggle must remain bound bidirectionally to dropdown state.");
            combo.IsEditable = true;
            Layout(combo, 200, 40);
            Require(((TextBox)combo.Template.FindName("PART_EditableTextBox", combo)).Visibility == Visibility.Visible, "Editable ComboBox must reveal the native editor.");

            var release = new RuntimeRelease
            {
                Id = "php:8.5.4:x64:nts", Kind = ServiceKind.Php, Version = "8.5.4", Channel = "8.5",
                DisplayName = "PHP 8.5.4 · x64 NTS", DownloadUri = new Uri("https://downloads.php.net/~windows/releases/fixture.zip")
            };
            var objectCombo = new ComboBox { ItemsSource = new[] { release }, DisplayMemberPath = nameof(RuntimeRelease.DisplayName), SelectedIndex = 0 };
            Layout(objectCombo, 300, 40);
            var selectedText = VisualChildren<TextBlock>(objectCombo).Select(block => block.Text).ToArray();
            Require(selectedText.Contains(release.DisplayName), "DisplayMemberPath must render the friendly RuntimeRelease name in the selected item presenter.");
            Require(!selectedText.Any(value => value.Contains("RuntimeRelease {", StringComparison.Ordinal)), "ComboBox selected content must not fall back to record ToString output.");
            var dropdownItem = new ComboBoxItem { Content = release, ContentTemplateSelector = objectCombo.ItemTemplateSelector };
            Layout(dropdownItem, 300, 40);
            var itemText = VisualChildren<TextBlock>(dropdownItem).Select(block => block.Text).ToArray();
            Require(itemText.Contains(release.DisplayName) && !itemText.Any(value => value.Contains("RuntimeRelease {", StringComparison.Ordinal)),
                "The dropdown item presenter must preserve the DisplayMemberPath selector too.");

            var checkbox = new CheckBox { Width = 40, Height = 40 };
            Layout(checkbox, 40, 40);
            var toggleProvider = (IToggleProvider)new CheckBoxAutomationPeer(checkbox).GetPattern(PatternInterface.Toggle)!;
            toggleProvider.Toggle();
            Require(checkbox.IsChecked == true, "Styled checkbox must retain its accessible native toggle pattern.");

            var list = new ListBox { ItemsSource = new[] { "First", "Second", "Third" }, SelectedIndex = 1 };
            Layout(list, 240, 140);
            Require((string)list.SelectedItem == "Second", "Styled list must retain native selection semantics.");
            var scroll = new ScrollBar { Orientation = Orientation.Horizontal, Minimum = 0, Maximum = 100, Value = 60, ViewportSize = 10 };
            Layout(scroll, 240, 12);
            Require(scroll.Template.FindName("PART_Track", scroll) is Track { Orientation: Orientation.Horizontal }, "Scrollbar must retain its native oriented track.");
            var menu = new MenuItem { Header = "View logs" };
            Layout(menu, 200, 40);
            menu.Items.Add(new MenuItem { Header = "Nested action" });
            Layout(menu, 200, 40);
            Require(VisualChildren<Popup>(menu).Any(), "Menu template must retain its submenu popup.");

            LogViewerChecks.Run();
            AutoRefreshChecks.Run();
            StartupChecks.Run();
            Require(AutomationProperties.GetName(DialogUi.ActionButton("Browse folders…", "SecondaryButtonStyle", AppIcon.Folder)) == "Browse folders…", "Dialog buttons with composed icon content need an explicit accessible name.");
            themeType.GetMethod("ApplyHighContrast", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [app.Resources]);
            Require(ReferenceEquals(app.Resources["TextBrush"], SystemColors.WindowTextBrush), "High contrast must use native system text colors.");
            Require(ReferenceEquals(app.Resources["SelectedTextBrush"], SystemColors.HighlightTextBrush), "High contrast selections must use native highlight text colors.");
            Require(app.Resources["PopupShadowEffect"] is null, "High contrast must remove decorative popup shadows.");
            Console.WriteLine("PASS: WPF design resources, cached theme switching, palette contrast, all vector geometries, inherited icon colors.");
            Console.WriteLine("PASS: WPF UI text/selection/toggle patterns, dropdowns, 40px controls and stable disabled geometry.");
            Console.WriteLine("PASS: RuntimeRelease objects render DisplayMemberPath in both collapsed ComboBox selection and dropdown items; no record ToString fallback.");
            Console.WriteLine("PASS: native high-contrast resource mapping and no decorative shadows. No system settings changed or windows opened.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Layout(FrameworkElement element, double width, double height)
    {
        if (element is Control control && control.Style is null && Application.Current.TryFindResource(element.GetType()) is Style style) control.Style = style;
        element.ApplyTemplate();
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }
    private static void CheckContrast(ResourceDictionary resources)
    {
        foreach (var pair in new[] { ("TextBrush", "CardBrush"), ("MutedBrush", "CardBrush"), ("MutedBrush", "SidebarBrush"), ("MutedBrush", "HoverBrush"), ("MutedBrush", "PressedBrush"), ("AccentTextBrush", "AccentBrush"), ("SelectedTextBrush", "SelectionBrush") })
        {
            var a = Luminance(((SolidColorBrush)resources[pair.Item1]).Color);
            var b = Luminance(((SolidColorBrush)resources[pair.Item2]).Color);
            var ratio = (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
            Require(ratio >= 4.5, $"Text palette {pair.Item1}/{pair.Item2} has insufficient contrast ({ratio:0.00}).");
        }
    }
    private static double Luminance(Color color)
    {
        static double Linear(byte component) { var value = component / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static IEnumerable<T> VisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T found) yield return found;
            foreach (var descendant in VisualChildren<T>(child)) yield return descendant;
        }
    }
}
