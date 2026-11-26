using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using PhpDevTray.Core;
using Microsoft.Win32;

namespace PhpDevTray.App;

internal sealed class ThemeManager : IDisposable
{
    private static readonly Dictionary<string, IReadOnlyDictionary<string, object>> Palettes = new();
    private ThemePreference _preference = ThemePreference.System;
    private string? _appliedPalette;
    private bool? _appliedMotion;
    private bool _disposed;

    public ThemeManager()
    {
        Apply();
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
    }

    public void SetPreference(ThemePreference preference)
    {
        if (!Enum.IsDefined(preference)) throw new ArgumentOutOfRangeException(nameof(preference));
        if (_preference == preference) return;
        _preference = preference;
        ApplyOnUiThread();
    }

    private void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Accessibility or UserPreferenceCategory.General)
            ApplyOnUiThread(invalidatePalette: true);
    }

    private void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.HighContrast) or nameof(SystemParameters.ClientAreaAnimation)) ApplyOnUiThread();
    }

    private void ApplyOnUiThread(bool invalidatePalette = false)
    {
        var application = Application.Current;
        if (_disposed || application is null || application.Dispatcher.HasShutdownStarted) return;
        void ApplyCurrent()
        {
            if (invalidatePalette) { Palettes.Clear(); _appliedPalette = null; }
            Apply();
        }
        if (application.Dispatcher.CheckAccess()) ApplyCurrent();
        else _ = application.Dispatcher.BeginInvoke(ApplyCurrent);
    }

    private void Apply()
    {
        if (_disposed || Application.Current is not { } application) return;
        var highContrast = SystemParameters.HighContrast;
        var palette = highContrast ? "HighContrast" : (_preference switch
        {
            ThemePreference.Dark => "Dark",
            ThemePreference.Light => "Light",
            _ => SystemUsesDarkTheme() ? "Dark" : "Light"
        });
        if (_appliedPalette != palette)
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(highContrast
                ? Wpf.Ui.Appearance.ApplicationTheme.HighContrast
                : palette == "Dark" ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light,
                Wpf.Ui.Controls.WindowBackdropType.Mica);
            if (highContrast) ApplyHighContrast(application.Resources);
            else
            {
                if (!Palettes.TryGetValue(palette, out var resources))
                    Palettes[palette] = resources = CreatePalette(application.Resources);
                foreach (var resource in resources) application.Resources[resource.Key] = resource.Value;
            }
            _appliedPalette = palette;
        }
        var motion = !highContrast && SystemParameters.ClientAreaAnimation;
        if (_appliedMotion != motion)
        {
            application.Resources["MotionEnabled"] = motion;
            _appliedMotion = motion;
        }
    }

    private static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException) { return false; }
    }

    // Product semantics are aliases of WPF UI's Fluent theme, not a second color system.
    private static IReadOnlyDictionary<string, object> CreatePalette(ResourceDictionary resources)
    {
        var background = ((SolidColorBrush)resources["ApplicationBackgroundBrush"]).Color;
        var surface = Resolve("NavigationViewContentBackground", background);
        var card = Resolve("CardBackground", surface);
        var aliases = new Dictionary<string, string>
        {
            ["SurfaceBrush"] = "NavigationViewContentBackground", ["CardBrush"] = "CardBackground",
            ["SidebarBrush"] = "ApplicationBackgroundBrush", ["TextBrush"] = "TextFillColorPrimaryBrush",
            ["MutedBrush"] = "TextFillColorSecondaryBrush", ["BorderBrush"] = "CardBorderBrush",
            ["InputBorderBrush"] = "ControlStrongStrokeColorDefaultBrush", ["DividerBrush"] = "DividerStrokeColorDefaultBrush",
            ["HoverBrush"] = "SubtleFillColorSecondaryBrush", ["PressedBrush"] = "SubtleFillColorTertiaryBrush",
            ["HoverBorderBrush"] = "ControlStrongStrokeColorDefaultBrush", ["AccentBrush"] = "AccentFillColorDefaultBrush",
            ["AccentHoverBrush"] = "AccentFillColorSecondaryBrush", ["AccentPressedBrush"] = "AccentFillColorTertiaryBrush",
            ["AccentTextBrush"] = "TextOnAccentFillColorPrimaryBrush", ["AccentSoftBrush"] = "SubtleFillColorSecondaryBrush",
            ["SelectionBrush"] = "SubtleFillColorSecondaryBrush", ["SelectedTextBrush"] = "TextFillColorPrimaryBrush",
            ["FocusBrush"] = "AccentFillColorDefaultBrush", ["ErrorBrush"] = "SystemFillColorCriticalBrush",
            ["SuccessBrush"] = "SystemFillColorSuccessBrush", ["WarningBrush"] = "SystemFillColorCautionBrush",
            ["DisabledTextBrush"] = "TextFillColorDisabledBrush", ["DisabledSurfaceBrush"] = "ControlFillColorDisabledBrush",
            ["ScrollThumbBrush"] = "ControlStrongFillColorDefaultBrush", ["ScrollThumbHoverBrush"] = "ControlStrongFillColorDefaultBrush"
        };
        var result = new Dictionary<string, object>();
        foreach (var (alias, key) in aliases)
        {
            var color = alias switch { "SurfaceBrush" => surface, "CardBrush" => card, _ => Resolve(key, card) };
            var brush = new SolidColorBrush(color); brush.Freeze(); result[alias] = brush;
        }
        result["LogSelectionBrush"] = result["SelectionBrush"];
        result["ListBoxItemSelectedBackgroundThemeBrush"] = result["SelectionBrush"];
        result["ListBoxItemSelectedForegroundThemeBrush"] = result["SelectedTextBrush"];
        // Fluent cards use borders rather than custom elevation effects.
        result["PopupShadowEffect"] = null!;
        return result;

        Color Resolve(string key, Color beneath)
        {
            var color = ((SolidColorBrush)resources[key]).Color;
            var alpha = color.A / 255d;
            return Color.FromRgb((byte)Math.Round(color.R * alpha + beneath.R * (1 - alpha)),
                (byte)Math.Round(color.G * alpha + beneath.G * (1 - alpha)),
                (byte)Math.Round(color.B * alpha + beneath.B * (1 - alpha)));
        }
    }
    private static void ApplyHighContrast(ResourceDictionary resources)
    {
        resources["LogSelectionBrush"] = SystemColors.WindowBrush;
        resources["ListBoxItemSelectedBackgroundThemeBrush"] = SystemColors.HighlightBrush;
        resources["ListBoxItemSelectedForegroundThemeBrush"] = SystemColors.HighlightTextBrush;
        foreach (var name in new[] { "SurfaceBrush", "CardBrush", "SidebarBrush", "DisabledSurfaceBrush", "HoverBrush", "PressedBrush", "AccentSoftBrush" }) resources[name] = SystemColors.WindowBrush;
        foreach (var name in new[] { "TextBrush", "MutedBrush", "BorderBrush", "InputBorderBrush", "DividerBrush", "ErrorBrush", "SuccessBrush", "WarningBrush" }) resources[name] = SystemColors.WindowTextBrush;
        foreach (var name in new[] { "AccentBrush", "AccentHoverBrush", "AccentPressedBrush", "SelectionBrush", "FocusBrush", "HoverBorderBrush" }) resources[name] = SystemColors.HighlightBrush;
        resources["AccentTextBrush"] = SystemColors.HighlightTextBrush;
        resources["SelectedTextBrush"] = SystemColors.HighlightTextBrush;
        resources["DisabledTextBrush"] = SystemColors.GrayTextBrush;
        resources["ScrollThumbBrush"] = SystemColors.WindowTextBrush;
        resources["ScrollThumbHoverBrush"] = SystemColors.HighlightBrush;
        resources["PopupShadowEffect"] = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
    }
}
