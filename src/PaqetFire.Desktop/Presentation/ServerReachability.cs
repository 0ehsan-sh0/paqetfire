using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PaqetFire.Desktop.Presentation;

public enum ServerPingStatus
{
    Replied,
    NoReply,
    InvalidEndpoint,
    NotResolved,
    ResolutionSkipped,
}

public sealed record ServerPingResult(
    ServerPingStatus Status,
    string Host,
    IPAddress? Address = null,
    long? AverageMilliseconds = null,
    int Replies = 0,
    int Attempts = 0);

/// <summary>
/// ICMP check of the Paqet server host from this PC. It measures plain network latency,
/// not the Paqet tunnel: many servers drop ping while Paqet still works.
/// </summary>
public sealed class ServerReachabilityProbe(
    Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null,
    Func<IPAddress, TimeSpan, Task<long?>>? ping = null)
{
    private const int Attempts = 3;
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(2);

    private readonly Func<string, CancellationToken, Task<IPAddress[]>> resolve =
        resolve ?? ((host, token) => Dns.GetHostAddressesAsync(host, token));

    private readonly Func<IPAddress, TimeSpan, Task<long?>> ping = ping ?? SendPingAsync;

    /// <param name="allowNameResolution">
    /// False while connected: captured DNS would return a FakeDNS placeholder or a tunnel-side answer.
    /// </param>
    public async Task<ServerPingResult> CheckAsync(
        string endpoint,
        bool allowNameResolution,
        CancellationToken cancellationToken = default)
    {
        if (!ServerReachabilityText.TryGetHost(endpoint, out var host))
        {
            return new(ServerPingStatus.InvalidEndpoint, endpoint.Trim());
        }

        if (!IPAddress.TryParse(host, out var address))
        {
            if (!allowNameResolution)
            {
                return new(ServerPingStatus.ResolutionSkipped, host);
            }

            try
            {
                var addresses = await resolve(host, cancellationToken).ConfigureAwait(false);
                address = addresses.FirstOrDefault(candidate => candidate.AddressFamily == AddressFamily.InterNetwork)
                    ?? addresses.FirstOrDefault();
            }
            catch (SocketException)
            {
                address = null;
            }

            if (address is null)
            {
                return new(ServerPingStatus.NotResolved, host);
            }
        }

        var samples = new List<long>();
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await ping(address, PingTimeout).ConfigureAwait(false) is { } milliseconds)
            {
                samples.Add(milliseconds);
            }
        }

        return samples.Count == 0
            ? new(ServerPingStatus.NoReply, host, address, Attempts: Attempts)
            : new(ServerPingStatus.Replied, host, address, (long)Math.Round(samples.Average()), samples.Count, Attempts);
    }

    private static async Task<long?> SendPingAsync(IPAddress address, TimeSpan timeout)
    {
        try
        {
            using var sender = new Ping();
            var reply = await sender.SendPingAsync(address, timeout).ConfigureAwait(false);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch (PingException)
        {
            return null;
        }
    }
}

public static class ServerReachabilityText
{
    public static bool TryGetHost(string? endpoint, out string host)
    {
        host = string.Empty;
        var candidate = endpoint?.Trim() ?? string.Empty;
        string hostText;
        string portText;
        if (candidate.StartsWith('['))
        {
            var bracket = candidate.IndexOf(']');
            if (bracket <= 1 || bracket + 2 >= candidate.Length || candidate[bracket + 1] != ':')
            {
                return false;
            }

            hostText = candidate[1..bracket];
            portText = candidate[(bracket + 2)..];
            if (!IPAddress.TryParse(hostText, out var ipv6) || ipv6.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return false;
            }
        }
        else
        {
            var separator = candidate.LastIndexOf(':');
            if (separator <= 0 || candidate[..separator].Contains(':'))
            {
                return false;
            }

            hostText = candidate[..separator];
            portText = candidate[(separator + 1)..];
            if (!IPAddress.TryParse(hostText, out _) && Uri.CheckHostName(hostText) != UriHostNameType.Dns)
            {
                return false;
            }
        }

        if (!int.TryParse(portText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > 65535)
        {
            return false;
        }

        host = hostText;
        return true;
    }

    public static string Format(ServerPingResult result) => result.Status switch
    {
        ServerPingStatus.Replied when result.Replies < result.Attempts =>
            $"{result.Host} answered {result.Replies} of {result.Attempts} pings · {result.AverageMilliseconds} ms average network latency.",
        ServerPingStatus.Replied =>
            $"{result.Host} answered · {result.AverageMilliseconds} ms average network latency.",
        ServerPingStatus.NoReply =>
            $"No ICMP reply from {result.Host} (the server may block ping). This does not mean Paqet will fail.",
        ServerPingStatus.NotResolved =>
            $"{result.Host} could not be resolved. Check the server name and this PC's internet connection.",
        ServerPingStatus.ResolutionSkipped =>
            "Disconnect first to test a server name; while connected, name lookups go through the route.",
        _ => "Enter the server as host:port first.",
    };

    public static string FormatTunnelLatency(string latencyText) =>
        $"Connected · tunnel latency {latencyText}, measured through Paqet. Disconnect to ping the server directly.";
}
