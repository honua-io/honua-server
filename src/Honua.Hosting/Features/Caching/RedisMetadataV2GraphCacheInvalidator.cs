// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Caching;
using StackExchange.Redis;

namespace Honua.Infrastructure.Caching;

// Publication is at-most-once, so the lifetime remains the correctness backstop, and the channel is prefix-scoped so installations that share one Redis do not invalidate each other. The graph-cache channel stays separate from honua:cache:invalidations because that channel's payload is a cache key the coordinator records as pending refresh work.
internal sealed partial class RedisMetadataV2GraphCacheInvalidator : IMetadataV2GraphCacheInvalidator, IHostedService
{
    internal const string ChannelSuffix = "metadata:v2:graph:invalidations";

    internal const string DefaultKeyPrefix = "honua:";

    internal const string AllEnvironments = "*";

    private const char Separator = '\n';

    private readonly MetadataV2GraphSnapshotCache _cache;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisMetadataV2GraphCacheInvalidator> _logger;
    private readonly string _instanceId;
    private readonly RedisChannel _channel;
    private ISubscriber? _subscriber;

    public RedisMetadataV2GraphCacheInvalidator(
        MetadataV2GraphSnapshotCache cache,
        IConnectionMultiplexer redis,
        ILogger<RedisMetadataV2GraphCacheInvalidator> logger,
        string? instanceId = null,
        string? keyPrefix = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _instanceId = string.IsNullOrWhiteSpace(instanceId)
            ? Environment.MachineName + "_" + Guid.NewGuid().ToString("N")
            : instanceId;
        ChannelName = BuildChannelName(keyPrefix);
        _channel = RedisChannel.Literal(ChannelName);
    }

    internal string ChannelName { get; }

    internal static string BuildChannelName(string? keyPrefix)
    {
        var prefix = string.IsNullOrWhiteSpace(keyPrefix) ? DefaultKeyPrefix : keyPrefix.Trim();
        if (!prefix.EndsWith(':'))
        {
            prefix += ":";
        }

        return prefix + ChannelSuffix;
    }

    public void Invalidate(string environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _cache.Invalidate(environment);
        Publish(environment);
    }

    public void InvalidateAll()
    {
        _cache.InvalidateAll();
        Publish(AllEnvironments);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var subscriber = _redis.GetSubscriber();
            await subscriber.SubscribeAsync(_channel, OnMessage).ConfigureAwait(false);
            _subscriber = subscriber;
            Log.SubscriptionStarted(_logger, ChannelName);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A subscription failure must not fail host startup: the node degrades to TTL-bounded
            // cross-node staleness (the pre-existing behaviour) and still invalidates its own cache.
            Log.SubscriptionFailed(_logger, ChannelName, ex);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscriber is not { } subscriber)
        {
            return;
        }

        try
        {
            await subscriber.UnsubscribeAsync(_channel, OnMessage).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.UnsubscribeFailed(_logger, ex);
        }
    }

    // A message this node published is ignored because the local cache was already cleared.
    internal void OnMessage(RedisChannel channel, RedisValue message)
    {
        try
        {
            var payload = message.ToString();
            var separatorIndex = payload.IndexOf(Separator, StringComparison.Ordinal);
            if (separatorIndex <= 0 || separatorIndex == payload.Length - 1)
            {
                Log.MalformedMessage(_logger, payload);
                return;
            }

            var origin = payload[..separatorIndex];
            if (string.Equals(origin, _instanceId, StringComparison.Ordinal))
            {
                return;
            }

            var environment = payload[(separatorIndex + 1)..];
            if (string.Equals(environment, AllEnvironments, StringComparison.Ordinal))
            {
                _cache.InvalidateAll();
            }
            else
            {
                _cache.Invalidate(environment);
            }

            Log.RemoteInvalidationApplied(_logger, environment, origin);
        }
        // Intentional: this is the pub/sub handler; a bad message must not tear down the subscription.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.RemoteInvalidationFailed(_logger, ex);
        }
    }

    private void Publish(string environment)
    {
        try
        {
            // Fire-and-forget: the write already committed and the local cache is cleared; peers fall
            // back to the TTL if this message is lost, so the write path never blocks on Redis.
            _redis.GetSubscriber().Publish(
                _channel,
                _instanceId + Separator + environment,
                CommandFlags.FireAndForget);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.PublishFailed(_logger, environment, ex);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(1121, LogLevel.Information, "Metadata graph cache invalidation subscribed on Redis channel {Channel}")]
        public static partial void SubscriptionStarted(ILogger logger, string channel);

        [LoggerMessage(1122, LogLevel.Warning, "Metadata graph cache invalidation subscription on Redis channel {Channel} failed; cross-node staleness is bounded by Cache:MetadataGraphTtlSeconds")]
        public static partial void SubscriptionFailed(ILogger logger, string channel, Exception exception);

        [LoggerMessage(1123, LogLevel.Warning, "Publishing a metadata graph cache invalidation for environment {Environment} failed; other nodes are bounded by Cache:MetadataGraphTtlSeconds")]
        public static partial void PublishFailed(ILogger logger, string environment, Exception exception);

        [LoggerMessage(1124, LogLevel.Debug, "Applied remote metadata graph cache invalidation for environment {Environment} from {Origin}")]
        public static partial void RemoteInvalidationApplied(ILogger logger, string environment, string origin);

        [LoggerMessage(1125, LogLevel.Warning, "Ignoring malformed metadata graph cache invalidation message {Message}")]
        public static partial void MalformedMessage(ILogger logger, string message);

        [LoggerMessage(1126, LogLevel.Warning, "Failed to apply a remote metadata graph cache invalidation")]
        public static partial void RemoteInvalidationFailed(ILogger logger, Exception exception);

        [LoggerMessage(1127, LogLevel.Debug, "Unsubscribing from the metadata graph cache invalidation channel failed")]
        public static partial void UnsubscribeFailed(ILogger logger, Exception exception);
    }
}
