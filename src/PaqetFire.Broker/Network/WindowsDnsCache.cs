using System.Runtime.InteropServices;

namespace PaqetFire.Broker.Network;

/// <summary>
/// Clears answers cached by the Windows DNS client, as <c>ipconfig /flushdns</c> does.
/// Route changes alter which resolver answers, and FakeDNS placeholder addresses are
/// only meaningful while the Xray process that issued them is running.
/// </summary>
public static class WindowsDnsCache
{
    public static bool TryFlush()
    {
        try
        {
            return DnsFlushResolverCache();
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
    private static extern bool DnsFlushResolverCache();
}
