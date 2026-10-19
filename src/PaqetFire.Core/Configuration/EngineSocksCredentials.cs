using System.Security.Cryptography;

namespace PaqetFire.Core.Configuration;

/// <summary>
/// Credentials for the loopback SOCKS5 hops between ProxiFyre, Xray, and Paqet, so other
/// local processes cannot borrow the route over TCP. They never leave the machine.
/// </summary>
public sealed record EngineSocksCredentials(string Username, string Password)
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    public static EngineSocksCredentials Generate() => new(
        "paqetfire-" + RandomNumberGenerator.GetString(Alphabet, 8),
        RandomNumberGenerator.GetString(Alphabet, 32));

    public override string ToString() => $"{nameof(EngineSocksCredentials)} {{ Username = {Username} }}";
}
