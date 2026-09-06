using PaqetFire.Core.Deployment;
using Xunit;

namespace PaqetFire.Core.Tests;

public sealed class PrerequisiteCompatibilityTests
{
    [Theory]
    [InlineData(461807, false)]
    [InlineData(461808, true)]
    [InlineData(533320, true)]
    public void DotNetFrameworkReleaseUses472AsMinimum(int release, bool expected) =>
        Assert.Equal(expected, PrerequisiteCompatibility.IsSupportedDotNetFrameworkRelease(release));

    [Theory]
    [InlineData("14.44.35210.0", false)]
    [InlineData("14.44.35211.0", true)]
    [InlineData("14.50.0.0", true)]
    public void VisualCppRuntimeUsesProxiFyreMinimum(string version, bool expected) =>
        Assert.Equal(
            expected,
            PrerequisiteCompatibility.IsSupportedVisualCppRuntimeVersion(Version.Parse(version)));

    [Theory]
    [InlineData("14.51.36231.0", "14.51.36231.0")]
    [InlineData("10.0.26100.8875 (WinBuild.160101.0800)", "10.0.26100.8875")]
    [InlineData("3.6.2.1 signed-driver-build", "3.6.2.1")]
    [InlineData("  14.44.35211.0  ", "14.44.35211.0")]
    public void WindowsFileVersionIgnoresMetadataSuffix(string value, string expected) =>
        Assert.Equal(
            Version.Parse(expected),
            PrerequisiteCompatibility.ParseWindowsFileVersion(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-version")]
    public void WindowsFileVersionRejectsMissingOrInvalidValues(string? value) =>
        Assert.Null(PrerequisiteCompatibility.ParseWindowsFileVersion(value));

    [Theory]
    [InlineData("3.6.0.9", false)]
    [InlineData("3.6.1.0", true)]
    [InlineData("3.9.9.9", true)]
    [InlineData("4.0.0.0", false)]
    public void WindowsPacketFilterRequiresCompatible3xDriver(string version, bool expected) =>
        Assert.Equal(
            expected,
            PrerequisiteCompatibility.IsSupportedWindowsPacketFilterVersion(Version.Parse(version)));
}
