using LocalServerManager.Core;

namespace LocalServerManager.Infrastructure.Downloads;

public sealed class DownloadedRuntime : IAsyncDisposable
{
    private readonly string _downloadsDirectory;
    private bool _disposed;

    internal DownloadedRuntime(ServiceKind kind, string executablePath, string stagingDirectory, string downloadsDirectory)
    {
        SafeArchiveExtractor.RequireDescendant(downloadsDirectory, stagingDirectory);
        SafeArchiveExtractor.RequireDescendant(stagingDirectory, executablePath);
        Kind = kind;
        ExecutablePath = executablePath;
        StagingDirectory = stagingDirectory;
        _downloadsDirectory = downloadsDirectory;
    }

    public ServiceKind Kind { get; }
    public string ExecutablePath { get; }
    public string StagingDirectory { get; }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            SafeArchiveExtractor.DeleteContainedDirectory(_downloadsDirectory, StagingDirectory);
            _disposed = true;
        }
        return ValueTask.CompletedTask;
    }
}
