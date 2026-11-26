using System.Windows;
using System.Windows.Controls;
using PhpDevTray.Core;
using Ui = Wpf.Ui.Controls;

namespace PhpDevTray.App;

internal sealed partial class ManagerWindow
{
    private bool _suppressNavigationEffects;
    private Task _navigationTask = Task.CompletedTask;
    private Page? _routedPage;

    private sealed class NavigationPages(ManagerWindow owner) : Wpf.Ui.Abstractions.INavigationViewPageProvider
    {
        private readonly Dictionary<Type, Page> _pages = [];
        private static readonly Dictionary<string, Type> Routes = new()
        {
            ["Overview"] = typeof(OverviewPage), ["Sites"] = typeof(SitesPage), ["Caddy"] = typeof(WebServerPage),
            ["PHP"] = typeof(PhpPage), ["Mail Trap"] = typeof(MailPage), ["Logs"] = typeof(ActivityPage),
            ["Updates"] = typeof(InstallPage), ["Settings"] = typeof(SettingsPage)
        };
        public static Type PageType(string key) => Routes[key];
        public object? GetPage(Type pageType)
        {
            if (!_pages.TryGetValue(pageType, out var page)) _pages[pageType] = page = (Page)Activator.CreateInstance(pageType)!;
            if (owner._routedPage is not null) owner._routedPage.Content = null;
            owner._routedPage = page; page.Content = owner._pageRoot;
            ScrollViewer.SetCanContentScroll(page, false);
            owner._section = Routes.Single(route => route.Value == pageType).Key;
            owner.Render(owner._manager.Snapshot);
            return page;
        }
    }
    private sealed class OverviewPage : Page { }
    private sealed class SitesPage : Page { }
    private sealed class WebServerPage : Page { }
    private sealed class PhpPage : Page { }
    private sealed class MailPage : Page { }
    private sealed class ActivityPage : Page { }
    private sealed class InstallPage : Page { }
    private sealed class SettingsPage : Page { }

    private async Task AfterNavigationAsync(string section)
    {
        if (section != "Updates") CancelReleaseCheck();
        try { await _manager.SetLastSectionAsync(section, _lifetime.Token); }
        catch (Exception error) { ShowError(error.Message); }
        if (section is "Mail Trap" or "Logs") await _pageRefresher.RefreshNowAsync();
        if (_section == "Updates" && !_releaseCheckComplete) await CheckReleasesAsync();
    }
    private void OnNotification(object? sender, NotificationMessage message)
    {
        if (_shutdown || Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!IsVisible || _shutdown) return;
            var appearance = message.Severity switch
            {
                NotificationSeverity.Error => Ui.ControlAppearance.Danger,
                NotificationSeverity.Warning => Ui.ControlAppearance.Caution,
                NotificationSeverity.Success => Ui.ControlAppearance.Success,
                _ => Ui.ControlAppearance.Info
            };
            _snackbars.Show(message.Title, message.Message, appearance, null, TimeSpan.FromSeconds(6));
        });
    }
}
