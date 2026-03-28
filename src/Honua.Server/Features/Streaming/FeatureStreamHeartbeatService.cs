// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Streaming;

/// <summary>
/// Background service that periodically sends heartbeat frames to all connected
/// feature-stream sessions. Heartbeat interval is shared across WebSocket and SSE transports.
/// </summary>
internal sealed class FeatureStreamHeartbeatService : BackgroundService
{
    private readonly FeatureStreamSessionManager _sessionManager;
    private readonly IOptions<FeatureStreamOptions> _options;
    private readonly ILogger<FeatureStreamHeartbeatService> _logger;

    public FeatureStreamHeartbeatService(
        FeatureStreamSessionManager sessionManager,
        IOptions<FeatureStreamOptions> options,
        ILogger<FeatureStreamHeartbeatService> logger)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = _options.Value.HeartbeatInterval;
        FeatureStreamLog.HeartbeatServiceStarted(_logger, interval);

        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                var sessions = _sessionManager.GetSessions();
                if (sessions.Count > 0)
                {
                    _sessionManager.BroadcastHeartbeat();
                    FeatureStreamLog.HeartbeatBroadcast(_logger, sessions.Count);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }

        FeatureStreamLog.HeartbeatServiceStopped(_logger);
    }
}
