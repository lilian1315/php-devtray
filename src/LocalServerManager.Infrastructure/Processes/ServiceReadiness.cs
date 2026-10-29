using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using LocalServerManager.Core;

namespace LocalServerManager.Infrastructure.Processes;

internal static class ServiceReadiness
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    internal static void VerifyPortsAvailable(IEnumerable<HealthEndpoint> endpoints)
    {
        foreach (var endpoint in endpoints.Where(endpoint => endpoint.Kind != HealthCheckKind.Process)
                     .DistinctBy(endpoint => (endpoint.Host, endpoint.Port)))
        {
            if (endpoint.Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(endpoint.Port));
            var address = ResolveAddress(endpoint.Host);
            var listener = new TcpListener(address, endpoint.Port);
            try
            {
                listener.Server.ExclusiveAddressUse = true;
                listener.Start();
            }
            catch (SocketException exception)
            {
                throw new InvalidOperationException($"Endpoint {endpoint.Host}:{endpoint.Port} is unavailable. Choose another port or stop the conflicting listener.", exception);
            }
            finally { listener.Stop(); }
        }
    }

    internal static async Task<bool> CheckAsync(OwnedProcess process, IReadOnlyList<HealthEndpoint> endpoints,
        CancellationToken cancellationToken)
    {
        if (process.HasExited) return false;
        foreach (var endpoint in endpoints)
        {
            if (endpoint.Kind == HealthCheckKind.Process) continue;
            using var probeDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probeDeadline.CancelAfter(TimeSpan.FromMilliseconds(900));
            try
            {
                if (!ListenerBelongsTo(process, ResolveAddress(endpoint.Host), endpoint.Port)) return false;
                if (!await ProbeAsync(endpoint, probeDeadline.Token).ConfigureAwait(false)) return false;
                // The preflight availability check is advisory; verify ownership again after probing.
                if (!ListenerBelongsTo(process, ResolveAddress(endpoint.Host), endpoint.Port)) return false;
            }
            catch (Exception exception) when (exception is SocketException or IOException or HttpRequestException or OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return false;
            }
        }
        return !process.HasExited;
    }

    private static async Task<bool> ProbeAsync(HealthEndpoint endpoint, CancellationToken cancellationToken)
    {
        if (endpoint.Kind == HealthCheckKind.Http)
        {
            var uri = new UriBuilder("http", endpoint.Host, endpoint.Port, endpoint.Path ?? "/").Uri;
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(ResolveAddress(endpoint.Host), endpoint.Port, cancellationToken).ConfigureAwait(false);
        if (endpoint.Kind == HealthCheckKind.Tcp) return true;
        await using var stream = client.GetStream();
        if (endpoint.Kind == HealthCheckKind.Smtp)
        {
            var greeting = new List<byte>();
            var singleByte = new byte[1];
            while (greeting.Count < 1024)
            {
                if (await stream.ReadAsync(singleByte, cancellationToken).ConfigureAwait(false) == 0) return false;
                greeting.Add(singleByte[0]);
                if (singleByte[0] == (byte)'\n') break;
            }
            var text = Encoding.ASCII.GetString(greeting.ToArray());
            await stream.WriteAsync("QUIT\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            return text.StartsWith("220 ", StringComparison.Ordinal) || text.StartsWith("220-", StringComparison.Ordinal);
        }
        if (endpoint.Kind == HealthCheckKind.FastCgi)
        {
            // FCGI_GET_VALUES must receive a protocol-valid FCGI_GET_VALUES_RESULT.
            // This establishes FastCGI protocol readiness, not application-script health.
            var body = new List<byte>();
            foreach (var name in new[] { "FCGI_MAX_CONNS", "FCGI_MAX_REQS", "FCGI_MPXS_CONNS" })
            {
                var encoded = Encoding.ASCII.GetBytes(name);
                body.Add((byte)encoded.Length);
                body.Add(0);
                body.AddRange(encoded);
            }
            var request = new byte[8 + body.Count];
            request[0] = 1;
            request[1] = 9;
            request[4] = (byte)(body.Count >> 8);
            request[5] = (byte)body.Count;
            body.CopyTo(request, 8);
            await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
            var header = new byte[8];
            await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            if (header[0] != 1 || header[1] != 10 || header[2] != 0 || header[3] != 0) return false;
            var contentLength = (header[4] << 8) | header[5];
            if (contentLength > 8192) return false;
            var responseBody = new byte[contentLength + header[6]];
            await stream.ReadExactlyAsync(responseBody, cancellationToken).ConfigureAwait(false);
            return true;
        }
        return false;
    }

    private static IPAddress ResolveAddress(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return IPAddress.Loopback;
        if (IPAddress.TryParse(host, out var address) && address.AddressFamily == AddressFamily.InterNetwork) return address;
        throw new ArgumentException("Health endpoints must use explicit IPv4 addresses or localhost.", nameof(host));
    }

    private static bool ListenerBelongsTo(OwnedProcess process, IPAddress expectedAddress, int expectedPort)
    {
        // TCP_TABLE_OWNER_PID_LISTENER / AF_INET. Check the Job, not merely a potentially reused PID.
        uint length = 0;
        var status = WindowsNative.GetExtendedTcpTable(0, ref length, false, 2, 3, 0);
        if (status != 122 && status != 0) throw new IOException($"Cannot inspect endpoint ownership (Windows error {status}).");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(checked((int)length));
            try
            {
                status = WindowsNative.GetExtendedTcpTable(buffer, ref length, false, 2, 3, 0);
                if (status == 122) continue;
                if (status != 0) throw new IOException($"Cannot inspect endpoint ownership (Windows error {status}).");
                var count = Marshal.ReadInt32(buffer);
                var found = false;
                for (var index = 0; index < count; index++)
                {
                    var row = buffer + 4 + index * 24;
                    var portBytes = BitConverter.GetBytes(Marshal.ReadInt32(row, 8));
                    var port = (portBytes[0] << 8) | portBytes[1];
                    if (port != expectedPort) continue;
                    var local = new IPAddress(BitConverter.GetBytes(Marshal.ReadInt32(row, 4)));
                    if (!local.Equals(expectedAddress) && !local.Equals(IPAddress.Any)) continue;
                    var pid = unchecked((uint)Marshal.ReadInt32(row, 20));
                    if (!process.OwnsProcess(pid)) return false;
                    found = true;
                }
                return found;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return false;
    }
}
