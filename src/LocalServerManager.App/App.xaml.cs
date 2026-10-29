using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LocalServerManager.Core;
using LocalServerManager.Infrastructure;

namespace LocalServerManager.App;

public partial class App : Application
{
    private Mutex? _instance;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _activationWait;
    private IEnvironmentManager? _manager;
    private TrayHost? _tray;
    private ManagerWindow? _popup;
    private ThemeManager? _theme;
    private bool _exiting;
    private bool _smokeTest;
    private string? _screenshotPath;
    private int _exitCode;
    private ThemePreference? _smokeTheme;
    private bool _interactivePreview;

    internal bool IsResourceCheckHost { get; init; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        if (IsResourceCheckHost) return;
        base.OnStartup(e);
        _smokeTest = e.Args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase);
        var previewProfile = Properties["DevelopmentPreviewProfile"] as string;
        _interactivePreview = previewProfile is not null || e.Args.Contains("--preview", StringComparer.OrdinalIgnoreCase);
        _screenshotPath = Argument(e.Args, "--screenshot");
        var dataDirectory = _smokeTest ? Argument(e.Args, "--smoke-test")
            : _interactivePreview ? Argument(e.Args, "--preview") ?? previewProfile : Argument(e.Args, "--data-directory");
        if ((_smokeTest || _interactivePreview) && (dataDirectory is null || !Path.IsPathFullyQualified(dataDirectory)))
        {
            Console.Error.WriteLine("Smoke mode requires --smoke-test <absolute-data-directory>.");
            Shutdown(2);
            return;
        }
        if ((_smokeTest || _interactivePreview) && Argument(e.Args, "--theme") is { } themeArgument)
        {
            if (!Enum.TryParse<ThemePreference>(themeArgument, true, out var preference) || !Enum.IsDefined(preference))
            {
                Console.Error.WriteLine("Theme must be System, Light, or Dark.");
                Shutdown(2);
                return;
            }
            _smokeTheme = preference;
        }
        var suffix = _smokeTest || _interactivePreview ? $".Preview.{Environment.ProcessId}" : string.Empty;
        var instanceName = @"Local\LocalServerManager.Instance" + suffix;
        var activationName = @"Local\LocalServerManager.Activate" + suffix;
        _instance = new Mutex(true, instanceName, out var created);
        if (!created)
        {
            try { using var other = EventWaitHandle.OpenExisting(activationName); other.Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherException;
        try
        {
            _theme = new ThemeManager();
            _manager = dataDirectory is null ? Bootstrapper.CreateDefault() : Bootstrapper.Create(dataDirectory);
            _tray = new TrayHost();
            _tray.OpenRequested += (_, _) => OpenManager();
            _tray.ToggleRequested += async (_, _) => await ToggleAllAsync();
            _tray.ExitRequested += async (_, _) => await ExitAsync();
            _manager.Changed += OnManagerChanged;
            _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, activationName);
            _activationWait = ThreadPool.RegisterWaitForSingleObject(_activationEvent,
                (_, _) => Dispatcher.BeginInvoke(OpenManager), null, Timeout.Infinite, false);
            await _manager.InitializeAsync();
            if ((_interactivePreview || _smokeTest) && _smokeTheme is { } previewPreference)
            {
                await _manager.SetThemeAsync(previewPreference);
                if (_interactivePreview) _smokeTheme = null;
            }
            RefreshUi();
            if (_smokeTest)
            {
                if (e.Args.Contains("--smoke-start-services", StringComparer.OrdinalIgnoreCase))
                    await _manager.StartAllAsync();
                if (Argument(e.Args, "--smoke-start-service") is { } serviceId)
                    await _manager.StartServiceAsync(serviceId);
                OpenManager();
                if (_popup is null) throw new InvalidOperationException("Smoke window was not created.");
                if (Argument(e.Args, "--smoke-width") is { } widthArgument)
                {
                    if (!double.TryParse(widthArgument, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var width) || width is < 480 or > 1600)
                        throw new ArgumentException("Smoke width must be between 480 and 1600 DIPs.");
                    _popup.MinWidth = width;
                    _popup.Width = width;
                }
                if (Argument(e.Args, "--smoke-height") is { } heightArgument)
                {
                    if (!double.TryParse(heightArgument, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var height) || height is < 400 or > 1200)
                        throw new ArgumentException("Smoke height must be between 400 and 1200 DIPs.");
                    _popup.MinHeight = height;
                    _popup.Height = height;
                }
                if (e.Args.Contains("--smoke-window-lifecycle", StringComparer.OrdinalIgnoreCase)) await _popup.VerifyWindowLifecycleForSmokeAsync();
                if (Argument(e.Args, "--smoke-section") is { } section)
                    await _popup.PrepareSmokeSectionAsync(section, e.Args.Contains("--smoke-check-releases", StringComparer.OrdinalIgnoreCase));
                if (Argument(e.Args, "--smoke-php-updates") is { } updateChannel)
                    await _popup.PreparePhpUpdatesForSmokeAsync(updateChannel);
                if (Argument(e.Args, "--smoke-site-php") is { } siteChoice)
                {
                    var choice = siteChoice.Split('|', 2); if (choice.Length != 2) throw new ArgumentException("Use hostname|PHP-channel for --smoke-site-php.");
                    await _popup.SetSitePhpForSmokeAsync(choice[0], choice[1]);
                }
                if (Argument(e.Args, "--smoke-mail-search") is { } mailQuery) _popup.SetMailSearchForSmoke(mailQuery);
                if (Argument(e.Args, "--smoke-error") is { } errorText) _popup.ShowError(errorText);
                Window capture = _popup;
                if (Argument(e.Args, "--smoke-extensions") is { } channel)
                {
                    var extensions = new ExtensionsDialog(_manager, channel, CancellationToken.None) { Owner = _popup };
                    await extensions.LoadForSmokeAsync();
                    extensions.Show();
                    capture = extensions;
                }
                else if (e.Args.Contains("--smoke-site-dialog", StringComparer.OrdinalIgnoreCase))
                {
                    var site = new SiteDialog(_manager.Snapshot.PhpInstallations.Select(p => p.Channel).ToArray()) { Owner = _popup };
                    site.Show();
                    capture = site;
                }
                if (Argument(e.Args, "--smoke-ready") is { } readyPath)
                {
                    if (!Path.IsPathFullyQualified(readyPath)) throw new ArgumentException("Smoke readiness path must be absolute.");
                    Directory.CreateDirectory(Path.GetDirectoryName(readyPath)!);
                    await File.WriteAllTextAsync(readyPath, "ready");
                }
                if (Argument(e.Args, "--smoke-wait-ms") is { } waitArgument)
                {
                    if (!int.TryParse(waitArgument, out var wait) || wait is < 0 or > 10000) throw new ArgumentException("Smoke wait must be between0 and10000 milliseconds.");
                    await Task.Delay(wait);
                }
                await Dispatcher.InvokeAsync(() => capture.UpdateLayout(), DispatcherPriority.ApplicationIdle);
                if (Argument(e.Args, "--smoke-expect-text") is { } expected) UiSmokeChecks.RequireText(capture, expected);
                if (e.Args.Contains("--verify-ui", StringComparer.OrdinalIgnoreCase))
                {
                    var checks = UiSmokeChecks.Validate(capture);
                    if (_screenshotPath is not null)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_screenshotPath))!);
                        File.WriteAllLines(_screenshotPath + ".qa.txt", checks);
                    }
                }
                if (_screenshotPath is not null) CaptureWindow(capture, _screenshotPath);
                if (capture != _popup) capture.Close();
                await ExitAsync();
            }
            else OpenManager();
        }
        catch (Exception exception)
        {
            _exitCode = 1;
            if (_smokeTest)
            {
                RecordSmokeFailure(exception);
            }
            else MessageBox.Show($"Local Server Manager could not start.\n\n{exception.Message}",
                    "Local Server Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            await ExitAsync();
        }
    }

    private void OnManagerChanged(object? sender, EventArgs e)
    {
        if (!_exiting) Dispatcher.BeginInvoke(RefreshUi);
    }

    private void RefreshUi()
    {
        if (_manager is null || _exiting) return;
        _theme?.SetPreference(_smokeTheme ?? _manager.Snapshot.Theme);
        _tray?.Update(_manager.Snapshot);
        if (_popup?.IsVisible == true) _popup.Render(_manager.Snapshot);
    }

    private void OpenManager()
    {
        if (_manager is null || _tray is null || _exiting) return;
        _popup ??= new ManagerWindow(_manager);
        if (_interactivePreview) _popup.ShowInTaskbar = true;
        _popup.RestoreWindow();
    }

    private async Task ToggleAllAsync()
    {
        if (_manager is null || _manager.Snapshot.IsBusy || _exiting) return;
        try
        {
            if (_manager.Snapshot.Services.Any(s => s.Status is ServiceStatus.Running or ServiceStatus.Starting))
                await _manager.StopAllAsync();
            else await _manager.StartAllAsync();
        }
        catch (Exception exception) { OpenManager(); _popup?.ShowError(exception.Message); }
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _popup?.BeginShutdown();
        if (_manager is not null)
        {
            _manager.Changed -= OnManagerChanged;
            try { await _manager.StopAllAsync().WaitAsync(TimeSpan.FromSeconds(35)); }
            catch (Exception exception) { System.Diagnostics.Debug.WriteLine(exception); }
            try { await _manager.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception exception) { System.Diagnostics.Debug.WriteLine(exception); }
        }
        try { _popup?.CloseForExit(); }
        finally { Shutdown(_exitCode); }
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        if (_smokeTest)
        {
            _exitCode = 1;
            RecordSmokeFailure(e.Exception);
            if (_exiting) Shutdown(_exitCode);
            else _ = ExitAsync();
            return;
        }
        if (_popup is not null) _popup.ShowError(e.Exception.Message);
        else MessageBox.Show(e.Exception.Message, "Local Server Manager", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationWait?.Unregister(null);
        _activationEvent?.Dispose();
        _tray?.Dispose();
        _theme?.Dispose();
        if (_instance is not null)
        {
            try { _instance.ReleaseMutex(); } catch (ApplicationException) { }
            _instance.Dispose();
        }
        base.OnExit(e);
    }

    private static string? Argument(string[] arguments, string name)
    {
        var index = Array.FindIndex(arguments, value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < arguments.Length && !arguments[index + 1].StartsWith("--", StringComparison.Ordinal)
            ? arguments[index + 1] : null;
    }

    private static void CaptureWindow(Window window, string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Screenshot path must be absolute.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = window.Content as FrameworkElement;
        var width = content is null ? window.ActualWidth : content.ActualWidth + content.Margin.Left + content.Margin.Right;
        var height = content is null ? window.ActualHeight : content.ActualHeight + content.Margin.Top + content.Margin.Bottom;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    private void RecordSmokeFailure(Exception exception)
    {
        Console.Error.WriteLine(exception);
        if (_screenshotPath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_screenshotPath))!);
            File.WriteAllText(_screenshotPath + ".error.txt", exception.ToString());
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
