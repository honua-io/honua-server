// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Hosting;

namespace Honua.Protocols.SensorThings.Streaming;

/// <summary>
/// Pulses observation-stream heartbeats so idle SSE/WebSocket connections stay alive
/// behind proxies. Lightweight: a single timer loop that fans a keep-alive frame out to
/// all sessions; transports also self-heartbeat on read timeout, so this is belt-and-braces.
/// </summary>
internal sealed class ObservationStreamHeartbeatService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(25);
    private readonly ObservationStreamSessionManager _sessionManager;

    public ObservationStreamHeartbeatService(ObservationStreamSessionManager sessionManager)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                if (_sessionManager.SessionCount > 0)
                {
                    _sessionManager.BroadcastHeartbeat();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }
}
