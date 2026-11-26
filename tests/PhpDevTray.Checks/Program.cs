using PhpDevTray.Checks;

if (await ProcessChecks.TryHandleChildModeAsync(args)) return 0;

try
{
    if (args.Contains("--real-web-setup", StringComparer.Ordinal))
    {
        if (args.Length != 2 && args.Length != 4) throw new ArgumentException("Expected --real-web-setup <path to NTS php.exe>");
        await WebSetupChecks.RunAsync(args[1], args.Length == 4 ? args[2] : null, args.Length == 4 ? args[3] : null);
        return 0;
    }
    if (args.Contains("--real-caddy", StringComparer.Ordinal))
    {
        await RealCaddyChecks.RunAsync(args);
        return 0;
    }
    if (args.Contains("--real-php-updates", StringComparer.Ordinal))
    {
        await RealPhpUpdateChecks.RunAsync(args);
        return 0;
    }
    if (args.Contains("--real-runtimes", StringComparer.Ordinal))
    {
        await RealRuntimeChecks.RunAsync(args);
        return 0;
    }
    await ApplicationStateChecks.RunAsync();
    await ApplicationChecks.RunAsync();
    await RuntimeChecks.RunAsync();
    await DownloadChecks.RunAsync();
    await ExtensionChecks.RunAsync();
    await PhpUpdateChecks.RunAsync();
    if (!args.Contains("--skip-process", StringComparer.Ordinal)) await ProcessChecks.RunAsync();
    Console.WriteLine("PASS: all requested checks completed.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"FAIL: {error}");
    return 1;
}
