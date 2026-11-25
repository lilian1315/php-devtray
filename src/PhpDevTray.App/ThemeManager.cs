using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using PhpDevTray.Core;
using Microsoft.Win32;

namespace PhpDevTray.App;

internal sealed class ThemeManager : IDisposable
{
    private static readonly IReadOnlyDictionary<string, object> LightPalette = CreatePalette(false);
    private static readonly IReadOnlyDictionary<string, object> DarkPalette = CreatePalette(true);
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
            ApplyOnUiThread(SystemParameters.HighContrast);
    }

    private void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.HighContrast) or nameof(SystemParameters.ClientAreaAnimation)) ApplyOnUiThread();
    }

    private void ApplyOnUiThread(bool invalidateHighContrast = false)
    {
        var application = Application.Current;
        if (_disposed || application is null || application.Dispatcher.HasShutdownStarted) return;
        void ApplyCurrent()
        {
            if (invalidateHighContrast) _appliedPalette = null;
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
            if (highContrast) ApplyHighContrast(application.Resources);
            else
            {
                foreach (var resource in palette == "Dark" ? DarkPalette : LightPalette) application.Resources[resource.Key] = resource.Value;
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

    private static IReadOnlyDictionary<string, object> CreatePalette(bool dark)
    {
        var colors = new Dictionary<string, string>
        {
            ["SurfaceBrush"] = dark ? "#17191E" : "#F7F8FA",
            ["CardBrush"] = dark ? "#20232A" : "#FFFFFF",
            ["SidebarBrush"] = dark ? "#13151A" : "#F0F2F5",
            ["TextBrush"] = dark ? "#F1F3F7" : "#202735",
            ["MutedBrush"] = dark ? "#ABB3C1" : "#5D6676",
            ["BorderBrush"] = dark ? "#363D49" : "#D9DFE8",
            ["InputBorderBrush"] = "#76849A",
            ["DividerBrush"] = dark ? "#2D333D" : "#E6EAF0",
            ["HoverBrush"] = dark ? "#2B313C" : "#E9EEF5",
            ["PressedBrush"] = dark ? "#343D4B" : "#DCE5F1",
            ["HoverBorderBrush"] = dark ? "#566174" : "#B4C0D1",
            ["AccentBrush"] = dark ? "#8BB4FF" : "#275DD7",
            ["AccentHoverBrush"] = dark ? "#A4C5FF" : "#2052C4",
            ["AccentPressedBrush"] = dark ? "#75A0ED" : "#1944A8",
            ["AccentTextBrush"] = dark ? "#10203C" : "#FFFFFF",
            ["AccentSoftBrush"] = dark ? "#25344D" : "#EAF0FE",
            ["SelectionBrush"] = dark ? "#293B58" : "#E8EFFD",
            ["SelectedTextBrush"] = dark ? "#BAD2FF" : "#2353BC",
            ["FocusBrush"] = dark ? "#A4C6FF" : "#3474E8",
            ["ErrorBrush"] = dark ? "#FFA1A7" : "#B82F3B",
            ["SuccessBrush"] = dark ? "#80D8B0" : "#19734E",
            ["WarningBrush"] = dark ? "#E9BE75" : "#8B601C",
            ["DisabledTextBrush"] = dark ? "#747F90" : "#8993A1",
            ["DisabledSurfaceBrush"] = dark ? "#252A33" : "#EDF0F5",
            ["ScrollThumbBrush"] = dark ? "#505B6D" : "#C2CBD7",
            ["ScrollThumbHoverBrush"] = dark ? "#76849A" : "#9DAABF"
        };
        var result = new Dictionary<string, object>();
        foreach (var color in colors)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color.Value));
            brush.Freeze();
            result[color.Key] = brush;
        }
        var shadow = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = Colors.Black, BlurRadius = 18, ShadowDepth = 4, Opacity = dark ? 0.3 : 0.13, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance
        };
        shadow.Freeze();
        result["PopupShadowEffect"] = shadow;
        return result;
    }

    private static void ApplyHighContrast(ResourceDictionary resources)
    {
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
