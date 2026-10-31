// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Honua.ControlPlane;

/// <summary>Client the serving process uses. Implementations must not talk to Docker themselves.</summary>
internal interface IComposeExecutorClient
{
    /// <summary>Sends one request and reads one response.</summary>
    Task<ComposeExecutorResponse> ExecuteAsync(ComposeExecutorRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Unix-socket client. One request per connection, ten-minute ceiling. A missing socket or a
/// truncated response throws <see cref="IOException"/> so rollback can surface the failure instead
/// of reporting success.
/// </summary>
internal sealed partial class ComposeSocketClient(
    IOptions<ComposeExecutorOptions> options,
    ILogger<ComposeSocketClient> logger) : IComposeExecutorClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(10);

    /// <inheritdoc />
    public async Task<ComposeExecutorResponse> ExecuteAsync(
        ComposeExecutorRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var socketPath = options.Value.SocketPath;
        if (string.IsNullOrWhiteSpace(socketPath))
        {
            throw new IOException("The Compose executor socket is not configured.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), timeout.Token).ConfigureAwait(false);
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            var line = JsonSerializer.Serialize(request, ComposeExecutorJsonContext.Default.ComposeExecutorRequest);
            var payload = Encoding.UTF8.GetBytes(line + "\n");
            await stream.WriteAsync(payload, timeout.Token).ConfigureAwait(false);
            var responseLine = await ReadLineAsync(stream, timeout.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(responseLine))
            {
                throw new IOException("The Compose executor closed the socket without a response.");
            }

            return JsonSerializer.Deserialize(responseLine, ComposeExecutorJsonContext.Default.ComposeExecutorResponse)
                ?? throw new IOException("The Compose executor returned an empty response.");
        }
        catch (Exception ex) when (ex is SocketException or JsonException)
        {
            Log.SocketFailed(logger, request.Op ?? string.Empty, ex.GetType().Name);
            throw new IOException("The Compose executor socket request failed.", ex);
        }
    }

    private static async Task<string> ReadLineAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var one = new byte[1];
        while (buffer.Length <= ComposeExecutorProtocol.MaxRequestBytes)
        {
            var read = await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
            if (read == 0 || one[0] == (byte)'\n')
            {
                break;
            }

            buffer.WriteByte(one[0]);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 9530, Level = LogLevel.Warning, Message = "Compose executor socket request failed for op {Op} ({ExceptionType}).")]
        public static partial void SocketFailed(ILogger logger, string op, string exceptionType);
    }
}
