using System.Windows.Controls;
using PhpDevTray.Core;
using Button = Wpf.Ui.Controls.Button;
namespace PhpDevTray.App;

internal sealed partial class ManagerWindow
{
    private readonly List<(Button Button, Func<bool> Running)> _restartButtons = [];
    private Button RestartButton(Func<Task> action, Func<bool> running, string name)
    {
        var button = ActionButton("", action, "IconButtonStyle", AppIcon.Restart, name, enabled: () => !Busy && running());
        _restartButtons.Add((button, running)); return button;
    }
    private IEnumerable<ServiceSnapshot> OverviewServices(ServiceKind kind) => _snapshot.Services.Where(service =>
        (service.Kind == kind || kind == ServiceKind.Php && service.Kind == ServiceKind.FrankenPhp) && service.Status != ServiceStatus.NotInstalled);
    private Task RestartServiceAsync(string id, string title) => RunAsync($"Restarting {title}…", () => _manager.RestartServiceAsync(id, _lifetime.Token));
    private Task RestartOverviewServiceAsync(ServiceKind kind) => RunAsync("Restarting services…", async () =>
    {
        foreach (var service in OverviewServices(kind).Where(service => service.Status == ServiceStatus.Running).ToArray())
            await _manager.RestartServiceAsync(service.Id, _lifetime.Token);
    });
    private Task RestartRunningServicesAsync() => RunAsync("Restarting running services…", async () =>
    {
        var services = _snapshot.Services.Where(service => service.Status == ServiceStatus.Running)
            .OrderBy(service => service.Kind == ServiceKind.Caddy).ToArray();
        await _manager.StopAllAsync(_lifetime.Token);
        foreach (var service in services) await _manager.StartServiceAsync(service.Id, _lifetime.Token);
    });
}
