using PaqetFire.Broker.Diagnostics;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Connections;
using PaqetFire.Core.Diagnostics;
using PaqetFire.Core.Engines;
using Xunit;

namespace PaqetFire.Broker.Tests;

public sealed class ConnectionVerifierTests
{
    [Fact]
    public async Task ConnectedRoute_ReportsEveryEnabledCheck()
    {
        var probe = new RecordingRouteProbe();
        var verifier = new ConnectionVerifier(probe);

        var report = await verifier.VerifyAsync(ConnectedStatus(), new PaqetFireSettings(), CancellationToken.None);

        Assert.True(report.IsHealthy);
        Assert.Equal(7, report.PassedCount);
        Assert.Equal(0, report.FailedCount);
        Assert.Equal(VerificationCheckStatus.Warning,
            report.Checks.Single(check => check.Kind == VerificationCheckKind.ApplicationCapture).Status);
        Assert.Equal(1, probe.LocalCalls);
        Assert.Equal(1, probe.TcpCalls);
        Assert.Equal(1, probe.UdpCalls);
        Assert.Equal(1, probe.Ipv6Calls);
        Assert.Equal(1, probe.DnsLeakCalls);
        Assert.Equal("198.51.100.10", report.Checks.Single(check => check.Kind == VerificationCheckKind.PublicAddress).ObservedValue);
    }

    [Fact]
    public async Task DisconnectedRoute_DoesNotContactExternalChecks()
    {
        var probe = new RecordingRouteProbe();
        var verifier = new ConnectionVerifier(probe);
        var stopped = new EngineStatus(EngineKind.Paqet, EngineState.Stopped);
        var status = new ConnectionStatus(
            ConnectionState.Disconnected,
            stopped,
            new EngineStatus(EngineKind.Xray, EngineState.Stopped),
            new EngineStatus(EngineKind.ProxiFyre, EngineState.Stopped));

        var report = await verifier.VerifyAsync(status, new PaqetFireSettings(), CancellationToken.None);

        Assert.False(report.IsHealthy);
        Assert.Equal(2, report.FailedCount);
        Assert.Equal(6, report.Checks.Count(check => check.Status == VerificationCheckStatus.Skipped));
        Assert.Equal(0, probe.TotalCalls);
    }

    [Fact]
    public async Task DisabledFamilies_AreExplicitlySkipped()
    {
        var probe = new RecordingRouteProbe();
        var verifier = new ConnectionVerifier(probe);
        var settings = new PaqetFireSettings { RouteUdp = false, RouteIpv6 = false };

        var report = await verifier.VerifyAsync(ConnectedStatus(), settings, CancellationToken.None);

        Assert.Equal(VerificationCheckStatus.Skipped,
            report.Checks.Single(check => check.Kind == VerificationCheckKind.UdpDns).Status);
        Assert.Equal(VerificationCheckStatus.Skipped,
            report.Checks.Single(check => check.Kind == VerificationCheckKind.Ipv6).Status);
        Assert.Equal(VerificationCheckStatus.Skipped,
            report.Checks.Single(check => check.Kind == VerificationCheckKind.DnsLeak).Status);
        Assert.Equal(0, probe.UdpCalls);
        Assert.Equal(0, probe.Ipv6Calls);
        Assert.Equal(0, probe.DnsLeakCalls);
    }

    [Fact]
    public async Task ConnectedRoute_WithUpstreamOpenVpn_RequiresRunningOpenVpn()
    {
        var probe = new RecordingRouteProbe();
        var verifier = new ConnectionVerifier(probe);
        var settings = new PaqetFireSettings
        {
            UpstreamOpenVpn = new UpstreamOpenVpnOptions { Enabled = true, OvpnConfigText = "client" },
        };

        // When OpenVPN is not running:
        var statusWithoutOpenVpn = ConnectedStatus();
        var reportWithoutOpenVpn = await verifier.VerifyAsync(statusWithoutOpenVpn, settings, CancellationToken.None);
        Assert.False(reportWithoutOpenVpn.IsHealthy);
        var engineCheckFailing = reportWithoutOpenVpn.Checks.Single(check => check.Kind == VerificationCheckKind.EngineChain);
        Assert.Equal(VerificationCheckStatus.Failed, engineCheckFailing.Status);
        Assert.Contains("OpenVPN", engineCheckFailing.Detail);

        // When OpenVPN is running:
        var statusWithOpenVpn = new ConnectionStatus(
            ConnectionState.Connected,
            new EngineStatus(EngineKind.Paqet, EngineState.Running),
            new EngineStatus(EngineKind.Xray, EngineState.Running),
            new EngineStatus(EngineKind.ProxiFyre, EngineState.Running),
            null,
            DateTimeOffset.UtcNow,
            new EngineStatus(EngineKind.OpenVpn, EngineState.Running));

        var reportWithOpenVpn = await verifier.VerifyAsync(statusWithOpenVpn, settings, CancellationToken.None);
        Assert.True(reportWithOpenVpn.IsHealthy);
        var engineCheckPassing = reportWithOpenVpn.Checks.Single(check => check.Kind == VerificationCheckKind.EngineChain);
        Assert.Equal(VerificationCheckStatus.Passed, engineCheckPassing.Status);
        Assert.Contains("OpenVPN", engineCheckPassing.Detail);

        var publicAddressCheck = reportWithOpenVpn.Checks.Single(check => check.Kind == VerificationCheckKind.PublicAddress);
        Assert.Contains("Upstream OpenVPN tunnel", publicAddressCheck.Detail);
    }

    private static ConnectionStatus ConnectedStatus() => new(
        ConnectionState.Connected,
        new EngineStatus(EngineKind.Paqet, EngineState.Running),
        new EngineStatus(EngineKind.Xray, EngineState.Running),
        new EngineStatus(EngineKind.ProxiFyre, EngineState.Running));

    private sealed class RecordingRouteProbe : IRouteProbe
    {
        public int LocalCalls { get; private set; }
        public int TcpCalls { get; private set; }
        public int UdpCalls { get; private set; }
        public int Ipv6Calls { get; private set; }
        public int DnsLeakCalls { get; private set; }
        public int TotalCalls => LocalCalls + TcpCalls + UdpCalls + Ipv6Calls + DnsLeakCalls;

        public ValueTask<RouteProbeResult> CheckDnsLeakAsync(
            IReadOnlyList<System.Net.IPAddress> networkResolvers,
            CancellationToken cancellationToken)
        {
            DnsLeakCalls++;
            return ValueTask.FromResult(new RouteProbeResult(true, "resolver differs", 5, "172.69.0.1"));
        }

        public ValueTask<RouteProbeResult> CheckLocalRouteAsync(CancellationToken cancellationToken)
        {
            LocalCalls++;
            return ValueTask.FromResult(new RouteProbeResult(true, "local ok", 1));
        }

        public ValueTask<RouteProbeResult> CheckTcpIpv4Async(CancellationToken cancellationToken)
        {
            TcpCalls++;
            return ValueTask.FromResult(new RouteProbeResult(true, "tcp ok", 2, "198.51.100.10"));
        }

        public ValueTask<RouteProbeResult> CheckUdpDnsAsync(CancellationToken cancellationToken)
        {
            UdpCalls++;
            return ValueTask.FromResult(new RouteProbeResult(true, "udp ok", 3));
        }

        public ValueTask<RouteProbeResult> CheckIpv6Async(CancellationToken cancellationToken)
        {
            Ipv6Calls++;
            return ValueTask.FromResult(new RouteProbeResult(true, "ipv6 ok", 4, "2001:db8::10"));
        }
    }
}
