using PaqetFire.Broker.Configuration;
using Xunit;

namespace PaqetFire.Core.Tests;

public sealed class AtomicConfigurationStoreTests
{
    [Fact]
    public void Constructor_CreatesDestinationParentDirectory_WhenItDoesNotExist()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "PFTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var missingDir = Path.Combine(tempRoot, "engines", "paqet", "x64");
            var destination = Path.Combine(missingDir, "config.yaml");

            Assert.False(Directory.Exists(missingDir));

            var store = new AtomicConfigurationStore(tempRoot, [destination]);

            Assert.True(Directory.Exists(missingDir));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void Constructor_RejectsDestinationOutsideProtectedRoot()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "PFTest-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "PFOutside-" + Guid.NewGuid().ToString("N"), "config.yaml");
        Directory.CreateDirectory(tempRoot);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() =>
                new AtomicConfigurationStore(tempRoot, [outside]));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
