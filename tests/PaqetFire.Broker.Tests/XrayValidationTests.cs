using System.Diagnostics;
using PaqetFire.Broker.Engines;
using Xunit;

namespace PaqetFire.Broker.Tests;

public sealed class XrayValidationTests
{
    [Fact]
    public async Task HangingValidationTimesOutAndTerminatesChild()
    {
        using var child = CreateHangingProcess();
        await Assert.ThrowsAsync<TimeoutException>(() => XrayProcessAdapter.RunValidationAsync(
            child, TimeSpan.FromMilliseconds(250), CancellationToken.None));
        Assert.True(child.HasExited);
    }

    [Fact]
    public async Task CancelledValidationTerminatesChildAndPreservesCancellation()
    {
        using var child = CreateHangingProcess();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => XrayProcessAdapter.RunValidationAsync(
            child, TimeSpan.FromSeconds(10), cancellation.Token));
        Assert.True(child.HasExited);
    }

    private static Process CreateHangingProcess() => new()
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "ping.exe"),
            Arguments = "-t 127.0.0.1",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        },
    };
}
