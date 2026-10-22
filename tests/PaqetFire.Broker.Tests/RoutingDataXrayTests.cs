using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using PaqetFire.Broker.Configuration;
using PaqetFire.Broker.Engines;
using PaqetFire.Broker.RoutingData;
using PaqetFire.Broker.Runtime;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Deployment;
using PaqetFire.Core.Engines;
using PaqetFire.Core.RoutingData;
using Xunit;

namespace PaqetFire.Broker.Tests;

public sealed class BundledEngineFactAttribute : FactAttribute
{
    public BundledEngineFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PAQETFIRE_TEST_PAYLOAD_ROOT")))
        {
            Skip = "Set PAQETFIRE_TEST_PAYLOAD_ROOT to a staged payload directory.";
        }
    }
}

// Downloads the live release from GitHub, so it runs only when asked for explicitly.
public sealed class RoutingDataDownloadFactAttribute : FactAttribute
{
    public RoutingDataDownloadFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PAQETFIRE_TEST_PAYLOAD_ROOT")) ||
            Environment.GetEnvironmentVariable("PAQETFIRE_TEST_ROUTING_DATA_DOWNLOAD") != "1")
        {
            Skip = "Set PAQETFIRE_TEST_PAYLOAD_ROOT and PAQETFIRE_TEST_ROUTING_DATA_DOWNLOAD=1 to download the live release.";
        }
    }
}

/// <summary>Runs the pinned Xray against routing data; needs a staged payload.</summary>
public sealed class RoutingDataXrayTests : IDisposable
{
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "PaqetFire-routing-xray", Guid.NewGuid().ToString("N"));

    [BundledEngineTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task XrayLoadsGeoDataFromTheSelectedDirectory(bool withFiles)
    {
        await using var xray = await CreateAdapterAsync();
        var assets = Path.Combine(scratch, "assets");
        Directory.CreateDirectory(assets);
        if (withFiles)
        {
            CopyBundledData(assets);
        }

        var probe = Path.Combine(scratch, "probe.json");
        await File.WriteAllTextAsync(probe, """
            {"outbounds":[{"tag":"direct","protocol":"freedom"}],
             "routing":{"rules":[{"type":"field","domain":["geosite:private"],"ip":["geoip:private"],"outboundTag":"direct"}]}}
            """);

        var validation = xray.ValidateRoutingDataAsync(assets, probe, CancellationToken.None);
        if (withFiles)
        {
            await validation;
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => validation);
        }
    }

    [BundledEngineFact]
    public async Task XrayReadsTheDataWhileItsVerifiedHandlesAreHeld()
    {
        await using var xray = await CreateAdapterAsync();
        var assets = Path.Combine(scratch, "assets");
        Directory.CreateDirectory(assets);
        CopyBundledData(assets);
        var files = RoutingDataRelease.FileNames
            .Select(name => new RoutingDataFile(
                name,
                new FileInfo(Path.Combine(assets, name)).Length,
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(assets, name))))))
            .ToArray();
        var probe = Path.Combine(scratch, "probe.json");
        await File.WriteAllTextAsync(probe, """
            {"outbounds":[{"tag":"direct","protocol":"freedom"}],
             "routing":{"rules":[{"type":"field","domain":["geosite:category-ir"],"ip":["geoip:ir"],"outboundTag":"direct"}]}}
            """);

        // The adapter holds these handles from verification until Xray is ready.
        await using (await new RoutingDataFileVerifier(ProtectedDirectoryPolicy.CurrentUser()).OpenAsync(new RoutingDataSet(assets, "202610220935", files), CancellationToken.None))
        {
            await xray.ValidateRoutingDataAsync(assets, probe, CancellationToken.None);
            Assert.ThrowsAny<IOException>(() => File.OpenWrite(Path.Combine(assets, "geoip.dat")).Dispose());
        }
    }

    [BundledEngineFact]
    public async Task TheBundledDataLacksTheMalwareCategories()
    {
        await using var xray = await CreateAdapterAsync();
        var assets = Path.Combine(scratch, "assets");
        Directory.CreateDirectory(assets);
        CopyBundledData(assets);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new XrayRoutingDataValidator(xray).ValidateAsync(assets, CancellationToken.None));

        Assert.StartsWith("Xray rejected the downloaded routing data", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(assets, "probe.json")));
    }

    [RoutingDataDownloadFact]
    public async Task TheLiveReleaseDownloadsVerifiesAndPassesXray()
    {
        await using var xray = await CreateAdapterAsync();
        using var source = new GitHubRoutingDataSource(EngineSocksCredentials.Generate());
        using var service = new RoutingDataService(
            new RoutingDataServiceOptions { BundledXrayVersion = BundledEngineOptions.XrayVersion },
            new RoutingDataStore(Path.Combine(scratch, "routing-data"), ProtectedDirectoryPolicy.CurrentUser()),
            source,
            new XrayRoutingDataValidator(xray),
            new RoutingDataFileVerifier(ProtectedDirectoryPolicy.CurrentUser()),
            NullLogger<RoutingDataService>.Instance);

        var outcome = await service.StartUpdate();

        Assert.True(outcome == RoutingDataUpdateOutcome.Updated, service.Status.LastError);
        Assert.Equal(RoutingDataOrigin.Updated, service.Status.Origin);
        var set = await service.ResolveForLaunchAsync(CancellationToken.None);
        Assert.All(set.Files, file => Assert.InRange(file.Size, 1024 * 1024, RoutingDataRelease.MaximumFileBytes));
        Assert.Equal(RoutingDataUpdateOutcome.UpToDate, await service.StartUpdate());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(scratch, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // The test was skipped before writing anything.
        }
    }

    private static string PayloadRoot => Path.GetFullPath(Environment.GetEnvironmentVariable("PAQETFIRE_TEST_PAYLOAD_ROOT")!);

    private static async Task<XrayProcessAdapter> CreateAdapterAsync()
    {
        var manifest = ProductPayloadManifest.Load(Path.Combine(PayloadRoot, "payload-manifest.json"));
        var engine = Assert.Single(manifest.Engines, engine => engine.Engine == EngineKind.Xray);
        foreach (var file in engine.Files)
        {
            await PayloadVerifier.VerifyAsync(PayloadRoot, file, CancellationToken.None);
        }

        var executable = Path.Combine(PayloadRoot, engine.EntryPoint);
        var paths = new RuntimePaths("unused", "unused", "unused", executable, "unused",
            "unused", "unused", "unused", "unused", "unused");
        return new XrayProcessAdapter(BundledEngineOptions.CreateXray(paths));
    }

    private static void CopyBundledData(string destination)
    {
        var bundled = Path.Combine(PayloadRoot, "engines", "xray", "x64");
        foreach (var name in RoutingDataRelease.FileNames)
        {
            File.Copy(Path.Combine(bundled, name), Path.Combine(destination, name));
        }
    }
}
