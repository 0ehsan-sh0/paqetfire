namespace PaqetFire.Broker.Network;

public sealed record NetworkEnvironment(
    string InterfaceName,
    string InterfaceGuid,
    string LocalIpv4Address,
    string GatewayIpv4Address,
    string GatewayMacAddress);
