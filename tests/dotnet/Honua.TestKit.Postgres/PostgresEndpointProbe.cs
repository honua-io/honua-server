// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.Sockets;
using Npgsql;

namespace Honua.TestKit;

internal static class PostgresEndpointProbe
{
    internal static readonly TimeSpan AcceptTimeout = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan ProbeDelay = TimeSpan.FromMilliseconds(200);

    internal static Task WaitUntilAcceptingAsync(string connectionString)
    {
        var endpoint = new NpgsqlConnectionStringBuilder(connectionString);
        var host = string.IsNullOrWhiteSpace(endpoint.Host) ? "127.0.0.1" : endpoint.Host;
        return WaitUntilAcceptingAsync(host, endpoint.Port, AcceptTimeout);
    }

    // pg_isready runs inside the container. Docker Desktop can still refuse the published host port after that.
    internal static async Task WaitUntilAcceptingAsync(string host, int port, TimeSpan timeout)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        while (!timeoutSource.IsCancellationRequested)
        {
            if (await TryConnectAsync(host, port, timeoutSource.Token).ConfigureAwait(false))
            {
                return;
            }

            if (!await DelayForNextProbeAsync(timeoutSource.Token).ConfigureAwait(false))
            {
                break;
            }
        }

        throw new SocketException((int)SocketError.TimedOut);
    }

    private static async Task<bool> TryConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<bool> DelayForNextProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ProbeDelay, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
