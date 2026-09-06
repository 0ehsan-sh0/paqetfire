namespace PaqetFire.Core.Configuration;

public interface IXrayConfigurationWriter
{
    string Write(XrayRoutingPolicy policy);
}
