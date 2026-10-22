using System.Net;
using System.Net.Sockets;
using PaqetFire.Core.Configuration;

namespace PaqetFire.Broker.Diagnostics;

public enum Ipv6Capability
{
    Unknown,
    Supported,
    Unsupported,
}

public interface IIpv6CapabilityProbe
{
    /// <summary>Whether the Paqet server can reach an IPv6 destination for us.</summary>
    ValueTask<Ipv6Capability> ProbeAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Sends one plain HTTP request to an IPv6 literal through the Paqet SOCKS listener. Any
/// HTTP answer proves the server has a working IPv6 path; the caller only trusts a failure
/// while the IPv4 tunnel is known to be healthy, since a dead tunnel fails this as well.
/// </summary>
public sealed class Ipv6CapabilityProbe : IIpv6CapabilityProbe, IDisposable
{
    private static readonly Uri Target = new("http://[2606:4700:4700::1111]/");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient client;

    public Ipv6CapabilityProbe(EngineSocksCredentials credentials)
    {
        client = new HttpClient(new SocketsHttpHandler
        {
            Proxy = new WebProxy(new Uri($"socks5://127.0.0.1:{XrayJsonConfigurationWriter.PaqetPort}"))
            {
                Credentials = new NetworkCredential(credentials.Username, credentials.Password),
            },
            UseProxy = true,
            ConnectTimeout = Timeout,
            PooledConnectionLifetime = TimeSpan.Zero,
            AllowAutoRedirect = false,
        })
        {
            Timeout = Timeout,
        };
    }

    public async ValueTask<Ipv6Capability> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, Target);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            return Ipv6Capability.Supported;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or SocketException ||
                                              exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return Ipv6Capability.Unsupported;
        }
    }

    public void Dispose() => client.Dispose();
}
