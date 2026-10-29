using LocalServerManager.Checks;

if (await ProcessChecks.TryHandleChildModeAsync(args)) return 0;

try
{
    if (args.Contains("--real-nginx-includes", StringComparer.Ordinal))
    {
        await RealNginxIncludeChecks.RunAsync(args);
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
