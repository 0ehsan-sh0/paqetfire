using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace PaqetFire.Broker.Network;

/// <summary>
/// Resolves A records by querying the adapter's resolvers from the broker's own socket.
/// The broker is excluded from capture; the Windows DNS client is not, so while ProxiFyre
/// runs, a system lookup would be sent into a route that may itself be down.
/// </summary>
public static class DirectDnsResolver
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(2);

    /// <summary>The longest a resolution may delay a connect when every resolver is silent.</summary>
    public static readonly TimeSpan ResolutionTimeout = TimeSpan.FromMilliseconds(1500);

    /// <summary>Queries every resolver at once; the first non-empty answer wins.</summary>
    public static ValueTask<IReadOnlyList<IPAddress>> ResolveIpv4Async(
        string host,
        IReadOnlyList<IPAddress> servers,
        CancellationToken cancellationToken) =>
        ResolveIpv4Async(
            host,
            servers.Where(server => server.AddressFamily == AddressFamily.InterNetwork)
                .Distinct()
                .Select(server => new IPEndPoint(server, 53))
                .ToArray(),
            ResolutionTimeout,
            cancellationToken);

    internal static async ValueTask<IReadOnlyList<IPAddress>> ResolveIpv4Async(
        string host,
        IReadOnlyList<IPEndPoint> servers,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (servers.Count == 0)
        {
            return [];
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var queries = servers.Select(server => QueryUntilCancelledAsync(server, host, deadline.Token)).ToList();
        try
        {
            while (queries.Count > 0)
            {
                var completed = await Task.WhenAny(queries).ConfigureAwait(false);
                queries.Remove(completed);
                var answer = await completed.ConfigureAwait(false);
                if (answer.Count > 0)
                {
                    return answer;
                }
            }
        }
        finally
        {
            await deadline.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(queries).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return [];

        static async Task<IReadOnlyList<IPAddress>> QueryUntilCancelledAsync(
            IPEndPoint server,
            string host,
            CancellationToken cancellationToken)
        {
            try
            {
                return await QueryAsync(server, host, 1, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return [];
            }
        }
    }

    /// <summary>Sends one query and returns its A records; an empty list means no usable answer.</summary>
    public static ValueTask<IReadOnlyList<IPAddress>> QueryAsync(
        IPAddress server,
        string host,
        ushort type,
        CancellationToken cancellationToken) =>
        QueryAsync(new IPEndPoint(server, 53), host, type, cancellationToken);

    private static async ValueTask<IReadOnlyList<IPAddress>> QueryAsync(
        IPEndPoint server,
        string host,
        ushort type,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(QueryTimeout);
        try
        {
            using var udp = new UdpClient(server.AddressFamily);
            udp.Connect(server);
            var id = (ushort)RandomNumberGenerator.GetInt32(ushort.MaxValue + 1);
            await udp.SendAsync(CreateQuery(id, host, type), deadline.Token).ConfigureAwait(false);
            while (true)
            {
                var received = await udp.ReceiveAsync(deadline.Token).ConfigureAwait(false);
                if (TryReadAddresses(received.Buffer, id, out var addresses))
                {
                    return addresses;
                }
            }
        }
        catch (Exception exception) when (exception is SocketException or ArgumentException ||
                                              exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return [];
        }
    }

    public static byte[] CreateQuery(ushort id, string host, ushort type)
    {
        var bytes = new List<byte>(32 + host.Length);
        bytes.AddRange([(byte)(id >> 8), (byte)id, 0x01, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0]);
        foreach (var label in host.TrimEnd('.').Split('.'))
        {
            var encoded = Encoding.ASCII.GetBytes(label);
            if (encoded.Length is 0 or > 63)
            {
                throw new ArgumentException("The host name is not a valid DNS name.", nameof(host));
            }

            bytes.Add((byte)encoded.Length);
            bytes.AddRange(encoded);
        }

        bytes.AddRange([0, (byte)(type >> 8), (byte)type, 0x00, 0x01]);
        return bytes.ToArray();
    }

    /// <summary>Reads the A records of a response to <paramref name="id"/>; false for other or malformed packets.</summary>
    public static bool TryReadAddresses(ReadOnlySpan<byte> message, ushort id, out IReadOnlyList<IPAddress> addresses)
    {
        addresses = [];
        if (message.Length < 12 || BinaryPrimitives.ReadUInt16BigEndian(message) != id || (message[2] & 0x80) == 0)
        {
            return false;
        }

        var questions = BinaryPrimitives.ReadUInt16BigEndian(message[4..]);
        var answers = BinaryPrimitives.ReadUInt16BigEndian(message[6..]);
        var offset = 12;
        for (var index = 0; index < questions; index++)
        {
            if (!SkipName(message, ref offset) || offset + 4 > message.Length) return false;
            offset += 4;
        }

        var found = new List<IPAddress>();
        for (var index = 0; index < answers; index++)
        {
            if (!SkipName(message, ref offset) || offset + 10 > message.Length) return false;
            var type = BinaryPrimitives.ReadUInt16BigEndian(message[offset..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(message[(offset + 8)..]);
            offset += 10;
            if (offset + length > message.Length) return false;
            if (type == 1 && length == 4)
            {
                found.Add(new IPAddress(message.Slice(offset, 4)));
            }

            offset += length;
        }

        addresses = found;
        return true;
    }

    private static bool SkipName(ReadOnlySpan<byte> message, ref int offset)
    {
        while (offset < message.Length)
        {
            var length = message[offset];
            if ((length & 0xC0) == 0xC0)
            {
                offset += 2;
                return offset <= message.Length;
            }

            offset += 1 + length;
            if (length == 0)
            {
                return true;
            }
        }

        return false;
    }
}
