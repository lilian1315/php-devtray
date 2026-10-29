using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using PhpDevTray.Core;
using Microsoft.Win32.SafeHandles;

namespace PhpDevTray.Infrastructure.Processes;

/// <summary>A process created suspended and contained before any application code can run.</summary>
internal sealed class OwnedProcess : IAsyncDisposable
{
    private readonly KernelHandle _job;
    private readonly KernelHandle _process;
    private readonly StreamReader _stdout;
    private readonly StreamReader _stderr;
    private readonly BoundedOutput _output;
    private readonly BoundedOutput _error;
    private readonly Task _outputPump;
    private readonly Task _errorPump;
    private int _disposed;

    private OwnedProcess(KernelHandle job, KernelHandle process, uint id, SafeFileHandle stdout,
        SafeFileHandle stderr, string? logDirectory, string logName)
    {
        _job = job;
        _process = process;
        Id = checked((int)id);
        _stdout = new StreamReader(new FileStream(stdout, FileAccess.Read, 4096, false), Encoding.UTF8, true);
        _stderr = new StreamReader(new FileStream(stderr, FileAccess.Read, 4096, false), Encoding.UTF8, true);
        _output = new BoundedOutput(logDirectory, logName + ".stdout.log");
        _error = new BoundedOutput(logDirectory, logName + ".stderr.log");
        _outputPump = PumpAsync(_stdout, _output);
        _errorPump = PumpAsync(_stderr, _error);
    }

    internal int Id { get; }
    internal bool HasExited => Volatile.Read(ref _disposed) != 0 || WindowsNative.WaitForSingleObject(_process, 0) == 0;
    internal string StandardOutput => _output.Text;
    internal string StandardError => _error.Text;
    internal int ExitCode => WindowsNative.GetExitCodeProcess(_process, out var value) ? unchecked((int)value) : -1;

    internal static OwnedProcess Start(ProcessCommand command, string? logDirectory = null, string logName = "process")
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows Job Objects require Windows.");
        if (string.IsNullOrWhiteSpace(command.ExecutablePath) || !Path.IsPathFullyQualified(command.ExecutablePath))
            throw new ArgumentException("An absolute executable path is required.", nameof(command));
        if (!File.Exists(command.ExecutablePath)) throw new FileNotFoundException("The executable does not exist.", command.ExecutablePath);
        var workingDirectory = Path.GetFullPath(command.WorkingDirectory ?? Path.GetDirectoryName(command.ExecutablePath)!);
        if (!Directory.Exists(workingDirectory)) throw new DirectoryNotFoundException(workingDirectory);
        if (command.Arguments.Any(a => a is null || a.Contains('\0'))) throw new ArgumentException("Process arguments cannot contain NUL.");
        var attributes = new WindowsNative.SecurityAttributes
        {
            Length = Marshal.SizeOf<WindowsNative.SecurityAttributes>(), InheritHandle = true
        };
        using var job = new HandleTransfer<KernelHandle>(WindowsNative.CreateJobObject(0, null));
        if (job.Value.IsInvalid) WindowsNative.ThrowLastError("Cannot create the process containment Job Object.");
        var limits = new WindowsNative.JobExtendedLimitInformation
        {
            BasicLimitInformation = new WindowsNative.JobBasicLimitInformation { LimitFlags = WindowsNative.KillOnJobClose }
        };
        if (!WindowsNative.SetInformationJobObject(job.Value, WindowsNative.JobObjectExtendedLimitInformation,
            ref limits, (uint)Marshal.SizeOf<WindowsNative.JobExtendedLimitInformation>()))
            WindowsNative.ThrowLastError("Cannot configure kill-on-close process containment.");

        if (!WindowsNative.CreatePipe(out var stdoutRead, out var stdoutWrite, ref attributes, 0))
            WindowsNative.ThrowLastError("Cannot create standard output pipe.");
        using var outputRead = new HandleTransfer<SafeFileHandle>(stdoutRead);
        using (stdoutWrite)
        {
            if (!WindowsNative.SetHandleInformation(stdoutRead, WindowsNative.HandleFlagInherit, 0))
                WindowsNative.ThrowLastError("Cannot make the output read handle private.");
            if (!WindowsNative.CreatePipe(out var stderrRead, out var stderrWrite, ref attributes, 0))
                WindowsNative.ThrowLastError("Cannot create standard error pipe.");
            using var errorRead = new HandleTransfer<SafeFileHandle>(stderrRead);
            using (stderrWrite)
            using (var input = WindowsNative.CreateFile("NUL", 0x80000000, 3, ref attributes, 3, 0, 0))
            {
                if (input.IsInvalid) WindowsNative.ThrowLastError("Cannot open the process input handle.");
                if (!WindowsNative.SetHandleInformation(stderrRead, WindowsNative.HandleFlagInherit, 0))
                    WindowsNative.ThrowLastError("Cannot make the error read handle private.");
                nint attributeList = 0, handleList = 0, environment = 0;
                var attributeInitialized = false;
                try
                {
                    nuint attributeSize = 0;
                    WindowsNative.InitializeProcThreadAttributeList(0, 1, 0, ref attributeSize);
                    attributeList = Marshal.AllocHGlobal(checked((nint)attributeSize));
                    if (!WindowsNative.InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeSize))
                        WindowsNative.ThrowLastError("Cannot initialize the process handle allowlist.");
                    attributeInitialized = true;
                    handleList = Marshal.AllocHGlobal(nint.Size * 3);
                    Marshal.WriteIntPtr(handleList, 0, input.DangerousGetHandle());
                    Marshal.WriteIntPtr(handleList, nint.Size, stdoutWrite.DangerousGetHandle());
                    Marshal.WriteIntPtr(handleList, nint.Size * 2, stderrWrite.DangerousGetHandle());
                    if (!WindowsNative.UpdateProcThreadAttribute(attributeList, 0, WindowsNative.ProcThreadAttributeHandleList,
                        handleList, (nuint)(nint.Size * 3), 0, 0))
                        WindowsNative.ThrowLastError("Cannot restrict inherited process handles.");

                    environment = Marshal.StringToHGlobalUni(BuildEnvironment(command.EnvironmentVariables));
                    var startup = new WindowsNative.StartupInfoEx
                    {
                        StartupInfo = new WindowsNative.StartupInfo
                        {
                            Size = Marshal.SizeOf<WindowsNative.StartupInfoEx>(), Flags = WindowsNative.StartfUseStdHandles,
                            StdInput = input.DangerousGetHandle(), StdOutput = stdoutWrite.DangerousGetHandle(),
                            StdError = stderrWrite.DangerousGetHandle()
                        },
                        AttributeList = attributeList
                    };
                    var commandLine = new StringBuilder(QuoteArgument(command.ExecutablePath));
                    foreach (var argument in command.Arguments) commandLine.Append(' ').Append(QuoteArgument(argument));
                    if (!WindowsNative.CreateProcess(command.ExecutablePath, commandLine, 0, 0, true,
                        WindowsNative.CreateSuspended | WindowsNative.CreateUnicodeEnvironment |
                        WindowsNative.CreateNoWindow | WindowsNative.ExtendedStartupInfoPresent,
                        environment, workingDirectory, ref startup, out var info))
                        WindowsNative.ThrowLastError("Cannot create the managed process.");
                    using var process = new HandleTransfer<KernelHandle>(new KernelHandle(info.Process));
                    using var thread = new KernelHandle(info.Thread);
                    if (!WindowsNative.AssignProcessToJobObject(job.Value, process.Value))
                    {
                        var error = Marshal.GetLastWin32Error();
                        WindowsNative.TerminateProcess(process.Value, 1);
                        WindowsNative.WaitForSingleObject(process.Value, 2000);
                        throw new Win32Exception(error, "Containment failed; the suspended process was terminated without running.");
                    }
                    if (WindowsNative.ResumeThread(thread) == uint.MaxValue)
                    {
                        var error = Marshal.GetLastWin32Error();
                        WindowsNative.TerminateJobObject(job.Value, 1);
                        throw new Win32Exception(error, "The contained process could not be resumed.");
                    }
                    // Only the three explicitly permitted pipe/NUL handles were inherited; never the Job handle.
                    // Close parent write ends before waiting for output-pipe EOF.
                    stdoutWrite.Dispose();
                    stderrWrite.Dispose();
                    var owned = new OwnedProcess(job.Value, process.Value, info.ProcessId,
                        outputRead.Value, errorRead.Value, logDirectory, logName);
                    job.Take();
                    process.Take();
                    outputRead.Take();
                    errorRead.Take();
                    return owned;
                }
                finally
                {
                    if (attributeInitialized) WindowsNative.DeleteProcThreadAttributeList(attributeList);
                    if (attributeList != 0) Marshal.FreeHGlobal(attributeList);
                    if (handleList != 0) Marshal.FreeHGlobal(handleList);
                    if (environment != 0) Marshal.FreeHGlobal(environment);
                }
            }
        }
    }

    internal async Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        while (!HasExited) await Task.Delay(30, cancellationToken).ConfigureAwait(false);
    }

    internal async Task WaitForTreeExitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _disposed) != 0) return;
            if (!WindowsNative.QueryInformationJobObject(_job, WindowsNative.JobObjectBasicAccountingInformation,
                out var accounting, (uint)Marshal.SizeOf<WindowsNative.JobAccountingInformation>(), 0))
                WindowsNative.ThrowLastError("Cannot query owned process-group state.");
            if (accounting.ActiveProcesses == 0) return;
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    internal bool OwnsProcess(uint processId)
    {
        if (Volatile.Read(ref _disposed) != 0) return false;
        using var process = WindowsNative.OpenProcess(WindowsNative.ProcessQueryLimitedInformation, false, processId);
        return !process.IsInvalid && WindowsNative.IsProcessInJob(process, _job, out var contained) && contained;
    }

    internal void Terminate()
    {
        if (Volatile.Read(ref _disposed) == 0 && !_job.IsClosed)
            WindowsNative.TerminateJobObject(_job, 1);
    }

    internal async Task DrainOutputAsync()
    {
        try { await Task.WhenAll(_outputPump, _errorPump).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException) { /* A child retaining stdout cannot block supervisor cleanup. */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        WindowsNative.TerminateJobObject(_job, 1);
        _job.Dispose();
        await DrainOutputAsync().ConfigureAwait(false);
        _stdout.Dispose();
        _stderr.Dispose();
        _output.Dispose();
        _error.Dispose();
        _process.Dispose();
    }

    internal static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') result.Append('\\', slashes * 2 + 1).Append('"');
            else result.Append('\\', slashes).Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static string BuildEnvironment(IReadOnlyDictionary<string, string> changes)
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry item in Environment.GetEnvironmentVariables())
            variables[(string)item.Key] = item.Value?.ToString() ?? "";
        foreach (var (key, value) in changes)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Contains('=') || key.Contains('\0') || value.Contains('\0'))
                throw new ArgumentException("Environment variable names and values are invalid.");
            variables[key] = value;
        }
        return string.Join('\0', variables.Select(item => item.Key + "=" + item.Value)) + "\0\0";
    }

    private static async Task PumpAsync(StreamReader reader, BoundedOutput output)
    {
        var buffer = new char[4096];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0) output.Append(buffer.AsSpan(0, count));
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
    }

    private sealed class HandleTransfer<T>(T value) : IDisposable where T : SafeHandle
    {
        private bool _taken;
        internal T Value { get; } = value;
        internal T Take() { _taken = true; return Value; }
        public void Dispose() { if (!_taken) Value.Dispose(); }
    }

    private sealed class BoundedOutput : IDisposable
    {
        private const int MaxCharacters = 1024 * 1024;
        private const long MaxLogBytes = 5 * 1024 * 1024;
        private readonly object _sync = new();
        private readonly StringBuilder _text = new();
        private readonly string? _path;
        private bool _disposed;
        internal BoundedOutput(string? directory, string name)
        {
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
                var safeName = string.Concat(name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
                _path = Path.Combine(directory, safeName);
            }
        }
        internal string Text { get { lock (_sync) return _text.ToString(); } }
        internal void Append(ReadOnlySpan<char> data)
        {
            lock (_sync)
            {
                if (_disposed) return;
                _text.Append(data);
                if (_text.Length > MaxCharacters) _text.Remove(0, _text.Length - MaxCharacters);
                if (_path is null) return;
                try
                {
                    if (File.Exists(_path) && new FileInfo(_path).Length > MaxLogBytes) File.Move(_path, _path + ".1", true);
                    File.AppendAllText(_path, data.ToString(), Encoding.UTF8);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // A full log disk must not block pipe draining and deadlock the child process.
                }
            }
        }
        public void Dispose() { lock (_sync) _disposed = true; }
    }
}
