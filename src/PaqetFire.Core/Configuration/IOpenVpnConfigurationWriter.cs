namespace PaqetFire.Core.Configuration;

public interface IOpenVpnConfigurationWriter
{
    string GenerateRuntimeConfig(
        string originalOvpnText,
        int socksProxyPort,
        string authFilePath,
        string adapterName = "PaqetFire-VPN");
}
