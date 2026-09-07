using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using PaqetFire.Core.Ipc;

namespace PaqetFire.Broker.Ipc;

public sealed class NamedPipeBrokerServer(
    IBrokerRequestHandler requestHandler,
    ILogger<NamedPipeBrokerServer> logger)
{
    private const int MaxConcurrentClients = 1;
    private const int BufferSize = 16 * 1024;
    private static readonly TimeSpan ListenerRetryDelay = TimeSpan.FromSeconds(1);

    private readonly ConcurrentDictionary<long, ClientConnection> clients = new();
    private long nextClientId;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeServerStream pipe;
                try
                {
                    pipe = CreatePipe();
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(
                        exception,
                        "The IPC listener could not be created; retrying in {DelaySeconds} second.",
                        ListenerRetryDelay.TotalSeconds);
                    await Task.Delay(ListenerRetryDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                var clientId = Interlocked.Increment(ref nextClientId);
                var connection = new ClientConnection(pipe);
                clients[clientId] = connection;

                // The desktop app keeps one long-lived connection. Waiting for that session
                // to finish avoids creating overlapping secured pipe instances, which can be
                // rejected by Windows when this process runs as LocalSystem.
                await HandleClientAsync(clientId, connection, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal server shutdown.
        }
    }

    public async ValueTask PublishAsync(
        BrokerEvent brokerEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(brokerEvent);

        if (brokerEvent.ProtocolVersion != IpcProtocol.Version)
        {
            throw new ArgumentException(
                $"Broker events must use protocol version {IpcProtocol.Version}.",
                nameof(brokerEvent));
        }

        var message = BrokerServerMessage.FromEvent(brokerEvent);
        foreach (var (clientId, connection) in clients.ToArray())
        {
            try
            {
                await connection.WriteAsync(message, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or ObjectDisposedException or InvalidOperationException)
            {
                logger.LogDebug(exception, "IPC client {ClientId} disconnected during event delivery.", clientId);
                await RemoveClientAsync(clientId, connection).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleClientAsync(
        long clientId,
        ClientConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && connection.IsConnected)
            {
                BrokerRequest? request;

                try
                {
                    request = await ReadRequestAsync(connection.Pipe, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is JsonException or InvalidDataException)
                {
                    logger.LogWarning(exception, "IPC client {ClientId} sent an invalid message.", clientId);
                    await connection.WriteAsync(
                            BrokerServerMessage.FromResponse(BrokerResponse.Failed(
                                Guid.Empty,
                                BrokerErrorCode.InvalidRequest,
                                "The request was malformed.")),
                            cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                if (request is null)
                {
                    return;
                }

                var validationError = Validate(request);
                var response = validationError ??
                    await DispatchSafelyAsync(request, cancellationToken).ConfigureAwait(false);

                await connection.WriteAsync(
                        BrokerServerMessage.FromResponse(response),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal server shutdown.
        }
        catch (IOException exception)
        {
            logger.LogDebug(exception, "IPC client {ClientId} disconnected.", clientId);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "IPC client {ClientId} failed unexpectedly.", clientId);
        }
        finally
        {
            await RemoveClientAsync(clientId, connection).ConfigureAwait(false);
        }
    }

    private async ValueTask<BrokerResponse> DispatchSafelyAsync(
        BrokerRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await requestHandler.HandleAsync(request, cancellationToken)
                .ConfigureAwait(false);

            if (response.RequestId != request.RequestId ||
                response.ProtocolVersion != IpcProtocol.Version)
            {
                logger.LogError("The broker request handler returned an invalid response envelope.");
                return BrokerResponse.Failed(
                    request.RequestId,
                    BrokerErrorCode.InternalError,
                    "The broker returned an invalid response.");
            }

            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Broker command {Command} failed.", request.Command);
            return BrokerResponse.Failed(
                request.RequestId,
                BrokerErrorCode.InternalError,
                "The broker could not complete the request.");
        }
    }

    private static BrokerResponse? Validate(BrokerRequest request)
    {
        if (request.RequestId == Guid.Empty)
        {
            return BrokerResponse.Failed(
                request.RequestId,
                BrokerErrorCode.InvalidRequest,
                "A non-empty request ID is required.");
        }

        if (request.ProtocolVersion != IpcProtocol.Version)
        {
            return BrokerResponse.Failed(
                request.RequestId,
                BrokerErrorCode.IncompatibleProtocol,
                $"Protocol version {IpcProtocol.Version} is required.");
        }

        if (!Enum.IsDefined(request.Command))
        {
            return BrokerResponse.Failed(
                request.RequestId,
                BrokerErrorCode.InvalidRequest,
                "The broker command is not supported.");
        }

        if (request.ConnectAfterSave && request.Command != BrokerCommand.SaveSettings)
        {
            return BrokerResponse.Failed(
                request.RequestId,
                BrokerErrorCode.InvalidRequest,
                "Connect-after-save is valid only for a settings request.");
        }

        return null;
    }

    private static async ValueTask<BrokerRequest?> ReadRequestAsync(
        PipeStream pipe,
        CancellationToken cancellationToken)
    {
        var lengthBuffer = new byte[sizeof(int)];
        var bytesRead = await ReadPrefixAsync(pipe, lengthBuffer, cancellationToken)
            .ConfigureAwait(false);
        if (bytesRead == 0)
        {
            return null;
        }

        if (bytesRead != lengthBuffer.Length)
        {
            throw new InvalidDataException("The IPC message length prefix was incomplete.");
        }

        var messageLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
        if (messageLength is <= 0 or > IpcProtocol.MaxMessageSizeBytes)
        {
            throw new InvalidDataException("The IPC message size is invalid.");
        }

        var payload = new byte[messageLength];
        await pipe.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Deserialize<BrokerRequest>(payload, IpcJson.SerializerOptions)
            ?? throw new JsonException("The IPC request was empty.");
    }

    private static async ValueTask<int> ReadPrefixAsync(
        PipeStream pipe,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var bytesRead = await pipe.ReadAsync(buffer[totalRead..], cancellationToken)
                .ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            totalRead += bytesRead;
        }

        return totalRead;
    }

    private async ValueTask RemoveClientAsync(long clientId, ClientConnection connection)
    {
        if (clients.TryRemove(new KeyValuePair<long, ClientConnection>(clientId, connection)))
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(CreateRule(WellKnownSidType.NetworkSid, PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(CreateRule(WellKnownSidType.LocalSystemSid, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(CreateRule(WellKnownSidType.BuiltinAdministratorsSid, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(CreateRule(WellKnownSidType.InteractiveSid, PipeAccessRights.ReadWrite, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            IpcProtocol.PipeName,
            PipeDirection.InOut,
            MaxConcurrentClients,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            BufferSize,
            BufferSize,
            security);
    }

    private static PipeAccessRule CreateRule(
        WellKnownSidType sidType,
        PipeAccessRights rights,
        AccessControlType accessControlType) =>
        new(new SecurityIdentifier(sidType, domainSid: null), rights, accessControlType);

    internal sealed class ClientConnection(NamedPipeServerStream pipe, TimeSpan? writeTimeout = null) : IAsyncDisposable
    {
        private readonly SemaphoreSlim writeLock = new(1, 1);
        private int disposed;
        private readonly TimeSpan writeTimeout = writeTimeout ?? TimeSpan.FromSeconds(5);

        public NamedPipeServerStream Pipe { get; } = pipe;

        public bool IsConnected => Pipe.IsConnected;

        public async ValueTask WriteAsync(
            BrokerServerMessage message,
            CancellationToken cancellationToken)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(message, IpcJson.SerializerOptions);
            while (payload.Length > IpcProtocol.MaxMessageSizeBytes && TryDropOldestLogs(ref message))
            {
                payload = JsonSerializer.SerializeToUtf8Bytes(message, IpcJson.SerializerOptions);
            }

            if (payload.Length > IpcProtocol.MaxMessageSizeBytes)
            {
                throw new InvalidDataException("The IPC response exceeds the maximum message size.");
            }

            var lengthBuffer = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(lengthBuffer, payload.Length);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(writeTimeout);
            var acquired = false;
            try
            {
                await writeLock.WaitAsync(deadline.Token).ConfigureAwait(false);
                acquired = true;
                await Pipe.WriteAsync(lengthBuffer, deadline.Token).ConfigureAwait(false);
                await Pipe.WriteAsync(payload, deadline.Token).ConfigureAwait(false);
                await Pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A partially written frame cannot be reused. Closing also releases the
                // request reader, allowing the listener to accept another desktop.
                await DisposeAsync().ConfigureAwait(false);
                throw new IOException("The IPC client did not read a response within the write deadline.");
            }
            finally
            {
                if (acquired)
                {
                    writeLock.Release();
                }
            }
        }

        private static bool TryDropOldestLogs(ref BrokerServerMessage message)
        {
            var snapshot = message.Response?.Snapshot ?? message.Event?.Snapshot;
            if (snapshot?.RecentLogs is not { Count: > 0 } logs)
            {
                return false;
            }

            var removeCount = Math.Max(1, logs.Count / 4);
            var trimmedSnapshot = snapshot with { RecentLogs = logs.Skip(removeCount).ToArray() };
            message = message.MessageType switch
            {
                BrokerMessageType.Response => message with
                {
                    Response = message.Response! with { Snapshot = trimmedSnapshot },
                },
                BrokerMessageType.Event => message with
                {
                    Event = message.Event! with { Snapshot = trimmedSnapshot },
                },
                _ => message,
            };
            return true;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                await Pipe.DisposeAsync().ConfigureAwait(false);
            }
            // Writers may still be unwinding and releasing the managed semaphore.
        }
    }
}
