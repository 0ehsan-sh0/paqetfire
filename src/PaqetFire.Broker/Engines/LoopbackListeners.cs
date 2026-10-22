using System.Net;
using System.Net.NetworkInformation;

namespace PaqetFire.Broker.Engines;

/// <summary>
/// Reads the TCP listener table instead of connecting. On Windows a connection to a closed
/// loopback port can be retried for a second or more before it is refused, so a
/// connect-based check either waits that long or needs a timeout that it then always pays.
/// </summary>
internal static class LoopbackListeners
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    public static bool IsListening(IPEndPoint endpoint)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(listener =>
                listener.Port == endpoint.Port &&
                (listener.Address.Equals(endpoint.Address) ||
                 listener.Address.Equals(IPAddress.Any) && endpoint.AddressFamily == listener.AddressFamily));
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }
}
