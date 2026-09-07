using PaqetFire.Broker.Runtime;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Deployment;
using Xunit;

namespace PaqetFire.Broker.Tests;

public sealed class RuntimeMetadataTests
{
    [Fact]
    public async Task PollingLoadsSettingsOnceAndSaveRefreshesProjection()
    {
        var cache = new SnapshotMetadataCache();
        var loads = 0;
        ValueTask<PaqetFireSettings?> Load(CancellationToken _) =>
            ValueTask.FromResult<PaqetFireSettings?>(new() { ProfileName = $"load-{++loads}", TransportKey = "secret" });
        for (var i = 0; i < 20; i++)
            Assert.True((await cache.GetSettingsAsync(Load, CancellationToken.None))!.HasTransportKey);
        Assert.Equal(1, loads);
        await cache.UpdateSettingsAsync(new() { ProfileName = "saved", TransportKey = "replacement" });
        Assert.Equal("saved", (await cache.GetSettingsAsync(Load, CancellationToken.None))!.ProfileName);
        Assert.Equal(1, loads);
    }

    [Fact]
    public async Task MissingSettingsAreCachedUntilSave()
    {
        var cache = new SnapshotMetadataCache();
        var loads = 0;
        ValueTask<PaqetFireSettings?> Load(CancellationToken _) { loads++; return ValueTask.FromResult<PaqetFireSettings?>(null); }
        Assert.Null(await cache.GetSettingsAsync(Load, CancellationToken.None));
        Assert.Null(await cache.GetSettingsAsync(Load, CancellationToken.None));
        Assert.Equal(1, loads);
        await cache.UpdateSettingsAsync(new() { ProfileName = "first" });
        Assert.NotNull(await cache.GetSettingsAsync(Load, CancellationToken.None));
    }

    [Fact]
    public void PrerequisitesExpireAndConnectCanForceFreshInspection()
    {
        var clock = new TestClock();
        var cache = new SnapshotMetadataCache(clock);
        var inspections = 0;
        IReadOnlyList<PrerequisiteStatus> Inspect() => [new("driver", "Driver", ++inspections > 1, "test")];
        Assert.False(cache.GetPrerequisites(Inspect)[0].IsInstalled);
        clock.Now = clock.Now.AddSeconds(29);
        Assert.False(cache.GetPrerequisites(Inspect)[0].IsInstalled);
        Assert.Equal(1, inspections);
        Assert.True(cache.GetPrerequisites(Inspect, forceRefresh: true)[0].IsInstalled);
        Assert.Equal(2, inspections);
        clock.Now = clock.Now.AddSeconds(30);
        cache.GetPrerequisites(Inspect);
        Assert.Equal(3, inspections);
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
