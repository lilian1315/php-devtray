using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using PhpDevTray.Core;
using Ui = Wpf.Ui.Controls;

namespace PhpDevTray.App;

internal sealed partial class ManagerWindow
{
    private void BuildSettings()
    {
        Heading("Settings", "Make PHP DevTray work the way you do.");
        var rows = new StackPanel();
        rows.Children.Add(T("Personalization", 20, true, margin: new Thickness(0, 0, 0, 12)));
        _appearancePicker = new ComboBox
        {
            ItemsSource = Enum.GetValues<ThemePreference>(), SelectedItem = _snapshot.Theme,
            MinWidth = 140, HorizontalAlignment = HorizontalAlignment.Right
        };
        AutomationProperties.SetName(_appearancePicker, "App theme");
        _appearancePicker.SelectionChanged += async (_, _) =>
        {
            if (!_updatingInputs && _appearancePicker.SelectedItem is ThemePreference theme && theme != _snapshot.Theme)
                await RunAsync("Changing appearance…", () => _manager.SetThemeAsync(theme, _lifetime.Token));
        };
        rows.Children.Add(SettingsRow(SettingHeader("App theme", "Choose Light, Dark, or System to follow Windows."), _appearancePicker, AppIcon.Monitor));

        rows.Children.Add(T("Startup", 20, true, margin: new Thickness(0, 28, 0, 12)));
        _startupSetting = new Ui.ToggleSwitch { OnContent = "On", OffContent = "Off", IsEnabled = false, MinWidth = 84 };
        AutomationProperties.SetName(_startupSetting, "Start PHP DevTray with Windows");
        _startupSetting.Click += async (_, _) =>
        {
            if (_updatingInputs || _startupChanging) return;
            var enabled = _startupSetting.IsChecked == true;
            _startupChanging = true;
            try { await RunAsync("Updating Windows startup…", async () => _startupState = await _startupRegistration.SetEnabledAsync(enabled)); }
            finally { _startupChanging = false; UpdateSettings(); }
        };
        var startupHeader = SettingHeader("Start PHP DevTray with Windows", "Start services in the tray when you sign in. The window stays closed.");
        _startupNotice = T("", 12, muted: true, margin: new Thickness(0, 8, 0, 0)); startupHeader.Children.Add(_startupNotice);
        rows.Children.Add(SettingsRow(startupHeader, _startupSetting, AppIcon.Play));
        rows.Children.Add(SettingsRow(SettingHeader("Windows startup settings", "Manage startup permissions and apps."),
            ActionButton("Open settings", () => OpenPathAsync("ms-settings:startupapps"), "SecondaryButtonStyle", AppIcon.ExternalLink, independent: true), AppIcon.Settings));
        _ = ReadStartupSettingAsync();

        rows.Children.Add(T("Command-line PHP", 20, true, margin: new Thickness(0, 28, 0, 12)));
        if (_snapshot.PhpInstallations.Count > 0)
        {
            _defaultPhpPicker = new ComboBox
            {
                ItemsSource = _snapshot.PhpInstallations.Select(installation => installation.Channel).Distinct().ToArray(),
                SelectedItem = _snapshot.DefaultPhpChannel, MinWidth = 120
            };
            AutomationProperties.SetName(_defaultPhpPicker, "Default command-line PHP version");
            _defaultPhpPicker.SelectionChanged += async (_, _) =>
            {
                if (!_updatingInputs && _defaultPhpPicker.SelectedItem is string channel && channel != _snapshot.DefaultPhpChannel)
                    await RunAsync("Changing default PHP…", () => _manager.SetDefaultPhpAsync(channel, _lifetime.Token));
            };
            rows.Children.Add(SettingsRow(SettingHeader("Default version", "PHP used by the independent command-line launcher."), _defaultPhpPicker, AppIcon.Terminal));
        }
        else rows.Children.Add(SettingsRow(SettingHeader("Default version", "Install PHP to choose a command-line version."),
            ActionButton("Install PHP", () => OpenInstallAsync(ServiceKind.Php)), AppIcon.Terminal));
        if (AppDeployment.IsPackaged)
            rows.Children.Add(SettingsRow(SettingHeader("PHP command alias", "Windows manages the php command for Store installations."),
                ActionButton("Manage alias", () => OpenPathAsync("ms-settings:appsfeatures-appexecutionalias"), "SecondaryButtonStyle", AppIcon.ExternalLink, independent: true), AppIcon.Code));
        else
        {
            _pathSetting = new Ui.ToggleSwitch { OnContent = "On", OffContent = "Off", MinWidth = 84, IsChecked = _snapshot.PathIntegrationEnabled };
            AutomationProperties.SetName(_pathSetting, "Enable PHP in the user PATH");
            _pathSetting.Click += async (_, _) =>
            {
                if (!_updatingInputs)
                {
                    var enabled = _pathSetting.IsChecked == true;
                    await RunAsync("Updating terminal integration…", () => _manager.SetPathIntegrationAsync(enabled, _lifetime.Token));
                }
            };
            rows.Children.Add(SettingsRow(SettingHeader("Make PHP available in terminals", "Applies to new terminal sessions."), _pathSetting, AppIcon.Code));
        }
        rows.Children.Add(T("Workspace", 20, true, margin: new Thickness(0, 28, 0, 12)));
        rows.Children.Add(SettingsRow(SettingHeader("Workspace files", _snapshot.DataDirectory),
            ActionButton("Open folder", () => OpenPathAsync(_snapshot.DataDirectory), "SecondaryButtonStyle", AppIcon.Folder, independent: true), AppIcon.Folder));
        _contentHost.Children.Add(Scroll(rows));
    }
}
