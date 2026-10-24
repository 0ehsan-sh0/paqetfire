using System.Text;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Profiles;
using Xunit;

namespace PaqetFire.Core.Tests;

public sealed class ProfileCatalogSecretProtectionTests
{
    private sealed class FakeSecretProtector : IProfileSecretProtector
    {
        public string Protect(string purpose, string clearText) =>
            $"enc:{purpose}:{Convert.ToBase64String(Encoding.UTF8.GetBytes(clearText))}";

        public string Unprotect(string purpose, string protectedText)
        {
            var prefix = $"enc:{purpose}:";
            if (protectedText.StartsWith(prefix))
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(protectedText[prefix.Length..]));
            }
            return protectedText;
        }
    }

    [Fact]
    public void WriteProtected_ProtectsOpenVpnPassword()
    {
        var id = Guid.NewGuid();
        var profile = new ConnectionProfile(id, new PaqetFireSettings
        {
            ProfileName = "Proton Canada",
            ServerEndpoint = "1.2.3.4:443",
            UpstreamOpenVpn = new UpstreamOpenVpnOptions
            {
                Enabled = true,
                Username = "user123",
                Password = "SuperSecretOpenVpnPassword",
                OvpnConfigText = "client\nremote 149.88.97.110 443\n"
            }
        });

        var catalog = ProfileCatalog.Create(id, profile.Settings);
        var protector = new FakeSecretProtector();
        var json = ProfileCatalogJson.WriteProtected(catalog, protector);

        Assert.DoesNotContain("SuperSecretOpenVpnPassword", json);
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("SuperSecretOpenVpnPassword")), json);

        var restored = ProfileCatalogJson.ReadProtected(json, protector);
        var restoredProfile = restored.Profiles.Single();
        Assert.Equal("SuperSecretOpenVpnPassword", restoredProfile.Settings.UpstreamOpenVpn.Password);
        Assert.Equal("user123", restoredProfile.Settings.UpstreamOpenVpn.Username);
        Assert.True(restoredProfile.Settings.UpstreamOpenVpn.Enabled);
    }

    [Fact]
    public void ReadProtected_IgnoresUnknownProperties_SuchAsLegacyExitProxy()
    {
        var id = Guid.NewGuid();
        var profile = new ConnectionProfile(id, new PaqetFireSettings
        {
            ProfileName = "Legacy Profile",
            ServerEndpoint = "1.2.3.4:443",
        });

        var catalog = ProfileCatalog.Create(id, profile.Settings);
        var protector = new FakeSecretProtector();
        var json = ProfileCatalogJson.WriteProtected(catalog, protector);
        var injectedJson = json.Replace("\"settings\": {", "\"settings\": { \"exitProxy\": { \"enabled\": true },", StringComparison.Ordinal);

        var restored = ProfileCatalogJson.ReadProtected(injectedJson, protector);
        var restoredProfile = restored.Profiles.Single();
        Assert.Equal("Legacy Profile", restoredProfile.Settings.ProfileName);
    }
}
