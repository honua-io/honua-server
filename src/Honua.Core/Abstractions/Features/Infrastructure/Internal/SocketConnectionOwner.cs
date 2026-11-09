// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.Sockets;

namespace Honua.Core.Features.Infrastructure.Internal;

internal sealed class SocketConnectionOwner : IDisposable
{
    private Socket? _socket;

    public SocketConnectionOwner(Socket socket)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
    }

    public Socket Socket => _socket ?? throw new ObjectDisposedException(nameof(SocketConnectionOwner));

    public NetworkStream TransferToNetworkStream()
    {
        var socket = Interlocked.Exchange(ref _socket, null)
            ?? throw new ObjectDisposedException(nameof(SocketConnectionOwner));

        try
        {
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _socket, null)?.Dispose();
    }
}
