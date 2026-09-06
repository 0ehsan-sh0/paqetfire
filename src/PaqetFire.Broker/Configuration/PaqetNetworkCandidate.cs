namespace PaqetFire.Broker.Configuration;

public sealed record PaqetNetworkCandidate(
    string InterfaceName,
    string InterfaceGuid,
    string LocalIpv4Address,
    string GatewayIpv4Address,
    string? GatewayMacAddress,
    string Description);
