// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Sockets;

namespace Honua.Core.Features.Infrastructure.Abstractions;

/// <summary>
/// Owns a socket until its lifetime is transferred to a network stream.
/// </summary>
public sealed class SocketConnectionOwner : IDisposable
{
    private Socket? _socket;

    /// <summary>
    /// Initializes a new socket owner.
    /// </summary>
    /// <param name="socket">The socket whose lifetime is owned.</param>
    public SocketConnectionOwner(Socket socket)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
    }

    /// <summary>
    /// Gets the owned socket.
    /// </summary>
    public Socket Socket => _socket ?? throw new ObjectDisposedException(nameof(SocketConnectionOwner));

    /// <summary>
    /// Transfers socket ownership to a network stream.
    /// </summary>
    /// <returns>A stream that owns and disposes the socket.</returns>
    public NetworkStream TransferToNetworkStream()
    {
        var socket = Interlocked.Exchange(ref _socket, null)
            ?? throw new ObjectDisposedException(nameof(SocketConnectionOwner));
        return new NetworkStream(socket, ownsSocket: true);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Interlocked.Exchange(ref _socket, null)?.Dispose();
    }
}
