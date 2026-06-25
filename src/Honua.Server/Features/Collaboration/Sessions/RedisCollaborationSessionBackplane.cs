// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace Honua.Server.Features.Collaboration.Sessions;

/// <summary>
/// Redis-backed cross-node fan-out for saved-map collaboration presence/cursor/follow events.
/// Mirrors the feature-stream cluster-broadcast pattern: locally-originated events are published
/// to a shared channel and peer nodes re-inject them into their local participant outboxes,
/// excluding their own echoes via the originating instance id.
/// </summary>
/// <remarks>
/// Best-effort: a transient Redis outage degrades to local-only delivery. The subscription is
/// established once on host start; publish swallows transport failures so a collaboration mutation
/// never fails because the backplane is unavailable.
/// </remarks>
internal sealed partial class RedisCollaborationSessionBackplane
    : ICollaborationSessionBackplane, IHostedService
{
    private static readonly RedisChannel BroadcastChannel =
        new("collaboration:session:broadcast", RedisChannel.PatternMode.Literal);

    private readonly IConnectionMultiplexer _redis;
    private readonly InMemoryCollaborationSessionService _sessions;
    private readonly ILogger<RedisCollaborationSessionBackplane> _logger;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private ISubscriber? _subscriber;
    private volatile bool _enabled;
    private int _publishFailureLogged;
    private int _receiveFailureLogged;

    public RedisCollaborationSessionBackplane(
        IConnectionMultiplexer redis,
        InMemoryCollaborationSessionService sessions,
        ILogger<RedisCollaborationSessionBackplane> logger)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _subscriber = _redis.GetSubscriber();
            _subscriber.Subscribe(BroadcastChannel, HandleBroadcast);
            _enabled = true;
            Log.BackplaneSubscribed(_logger);
        }
        catch (Exception ex)
        {
            // Redis-less or unreachable: stay disabled and fall back to local-only delivery.
            Log.BackplaneUnavailable(_logger, ex);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _subscriber?.Unsubscribe(BroadcastChannel, HandleBroadcast);
        }
        catch (Exception ex)
        {
            Log.BackplaneUnavailable(_logger, ex);
        }

        _enabled = false;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Publish(CollaborationEventEnvelope ev)
    {
        var subscriber = _subscriber;
        if (!_enabled || subscriber is null)
        {
            return;
        }

        try
        {
            var payload = JsonSerializer.Serialize(
                new CollaborationBackplaneMessage { OriginInstanceId = _instanceId, Event = ev },
                CollaborationSessionJsonContext.Default.CollaborationBackplaneMessage);

            // Fire-and-forget: collaboration fan-out must never block on the backplane.
            subscriber.Publish(BroadcastChannel, payload, CommandFlags.FireAndForget);
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _publishFailureLogged, 1) == 0)
            {
                Log.BackplanePublishFailed(_logger, ex);
            }
        }
    }

    private void HandleBroadcast(RedisChannel channel, RedisValue value)
    {
        if (!_enabled || value.IsNullOrEmpty)
        {
            return;
        }

        try
        {
            var message = JsonSerializer.Deserialize(
                value.ToString(),
                CollaborationSessionJsonContext.Default.CollaborationBackplaneMessage);

            if (message is null ||
                string.Equals(message.OriginInstanceId, _instanceId, StringComparison.Ordinal))
            {
                // Own echo or malformed payload — ignore.
                return;
            }

            _sessions.ApplyRemoteEvent(message.Event);
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _receiveFailureLogged, 1) == 0)
            {
                Log.BackplaneReceiveFailed(_logger, ex);
            }
        }
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 7440,
            Level = LogLevel.Information,
            Message = "Collaboration session backplane subscribed to the Redis broadcast channel.")]
        public static partial void BackplaneSubscribed(ILogger logger);

        [LoggerMessage(
            EventId = 7441,
            Level = LogLevel.Warning,
            Message = "Collaboration session backplane is unavailable; falling back to local-only delivery.")]
        public static partial void BackplaneUnavailable(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 7442,
            Level = LogLevel.Warning,
            Message = "Collaboration session backplane publish failed; cross-node fan-out is degraded.")]
        public static partial void BackplanePublishFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 7443,
            Level = LogLevel.Warning,
            Message = "Collaboration session backplane received a malformed broadcast.")]
        public static partial void BackplaneReceiveFailed(ILogger logger, Exception exception);
    }
}
