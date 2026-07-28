// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly IServiceProvider _services;
    private readonly ILogger<RedisCollaborationSessionBackplane> _logger;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private InMemoryCollaborationSessionService? _sessions;
    private ISubscriber? _subscriber;
    private volatile bool _enabled;
    private int _publishFailureLogged;
    private int _receiveFailureLogged;

    // The session service and this backplane are mutually dependent — the service
    // publishes locally-originated events through the backplane, and the backplane
    // re-injects peer broadcasts into the service. Taking InMemoryCollaborationSessionService
    // directly in the constructor closes that loop into a DI construction cycle that, in
    // the Redis-configured topology, recurses through the singleton-cache lock until the
    // host hangs (honua-server#1841 regression surfaced via the Operator-Eval shard).
    // Resolving the service lazily from IServiceProvider here breaks the construction cycle:
    // the backplane only needs the service inside HandleBroadcast, which never runs before
    // the graph is fully built.
    public RedisCollaborationSessionBackplane(
        IConnectionMultiplexer redis,
        IServiceProvider services,
        ILogger<RedisCollaborationSessionBackplane> logger)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Resolve the session service now that the DI graph is fully built; taking it
            // in the constructor would close a backplane <-> session-service dependency cycle.
            _sessions = _services.GetRequiredService<InMemoryCollaborationSessionService>();
            _subscriber = _redis.GetSubscriber();
            _subscriber.Subscribe(BroadcastChannel, HandleBroadcast);
            _enabled = true;
            Log.BackplaneSubscribed(_logger);
        }
        // Intentionally generic: startup subscribe is best-effort (see class remarks);
        // Redis-less or unreachable: stay disabled and fall back to local-only delivery.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
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
        // Intentionally generic: shutdown unsubscribe is best-effort; a failed
        // unsubscribe must not block host shutdown.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.BackplaneUnavailable(_logger, ex);
        }

        _enabled = false;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public bool IsDistributed => true;

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
        // Intentionally generic: a collaboration mutation must never fail because
        // the Redis backplane is unavailable (see class remarks); the failure is
        // rate-limited-logged and swallowed so local delivery still proceeds.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (Interlocked.Exchange(ref _publishFailureLogged, 1) == 0)
            {
                Log.BackplanePublishFailed(_logger, ex);
            }
        }
    }

    private void HandleBroadcast(RedisChannel channel, RedisValue value)
    {
        var sessions = _sessions;
        if (!_enabled || sessions is null || value.IsNullOrEmpty)
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

            sessions.ApplyRemoteEvent(message.Event);
        }
        // Intentionally generic: this is the Redis pub/sub receive callback for a
        // peer broadcast; a malformed or unexpected payload must not crash the
        // subscription, so it is rate-limited-logged and dropped.
        catch (Exception ex) when (ex is not OutOfMemoryException)
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
