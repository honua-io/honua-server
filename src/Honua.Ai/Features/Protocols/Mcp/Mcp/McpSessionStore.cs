// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using StackExchange.Redis;

namespace Honua.Ai.Protocols.Mcp;

// Notification channels, lifetime tokens, and job indexes stay off this record because they back an SSE stream held open on one node.
internal sealed record McpSessionRecord(
    string SessionId,
    string PrincipalKey,
    bool ElicitationSupported,
    string? WorkflowView,
    DateTimeOffset CreatedUtc,
    int SchemaVersion = McpSessionRecord.CurrentSchemaVersion)
{
    public const int CurrentSchemaVersion = 1;
}

internal interface IMcpSessionStore : IMcpRequestStore
{
    Task SaveAsync(McpSessionRecord record, TimeSpan idleTimeout, CancellationToken cancellationToken);

    Task<McpSessionRecord?> GetAndTouchAsync(string sessionId, TimeSpan idleTimeout, CancellationToken cancellationToken);

    Task<McpSessionRecord?> GetAsync(string sessionId, CancellationToken cancellationToken);

    Task<bool> TouchAsync(string sessionId, TimeSpan idleTimeout, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(string sessionId, CancellationToken cancellationToken);

    Task RemoveAsync(string sessionId, CancellationToken cancellationToken);

    IDisposable? SubscribeToRemovals(Action<string> onRemoved);
}

internal sealed partial class RedisMcpSessionStore(IConnectionMultiplexer redis) : IMcpSessionStore
{
    internal const string KeyPrefix = "mcp:session:";

    internal static RedisKey Key(string sessionId) => KeyPrefix + sessionId;

    public async Task SaveAsync(McpSessionRecord record, TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();
        var payload = JsonSerializer.Serialize(record, McpSessionStoreJsonContext.Default.McpSessionRecord);
        await redis.GetDatabase()
            .StringSetAsync(Key(record.SessionId), payload, idleTimeout)
            .ConfigureAwait(false);
    }

    public async Task<McpSessionRecord?> GetAndTouchAsync(
        string sessionId,
        TimeSpan idleTimeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var database = redis.GetDatabase();
        var key = Key(sessionId);

        // Pipelined: slide the expiry and read the record in one round trip. An
        // expiry on a missing key is a no-op, so an unknown id stays unknown.
        var touch = database.KeyExpireAsync(key, idleTimeout);
        var read = database.StringGetAsync(key);
        await Task.WhenAll(touch, read).ConfigureAwait(false);

        var payload = read.Result;
        if (!payload.HasValue)
        {
            return null;
        }

        return ReadRecord(sessionId, payload);
    }

    public async Task<McpSessionRecord?> GetAsync(string sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = await redis.GetDatabase().StringGetAsync(Key(sessionId)).ConfigureAwait(false);
        return ReadRecord(sessionId, payload);
    }

    private static McpSessionRecord? ReadRecord(string sessionId, RedisValue payload)
    {
        if (!payload.HasValue)
        {
            return null;
        }

        var record = JsonSerializer.Deserialize(payload.ToString(), McpSessionStoreJsonContext.Default.McpSessionRecord);
        return record is not null && string.Equals(record.SessionId, sessionId, StringComparison.Ordinal)
            ? record
            : null;
    }

    public Task<bool> TouchAsync(string sessionId, TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return redis.GetDatabase().KeyExpireAsync(Key(sessionId), idleTimeout);
    }

    public Task<bool> ExistsAsync(string sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return redis.GetDatabase().KeyExistsAsync(Key(sessionId));
    }

    public async Task RemoveAsync(string sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await redis.GetDatabase().KeyDeleteAsync(Key(sessionId)).ConfigureAwait(false);
        await redis.GetSubscriber().PublishAsync(RemovalChannel, sessionId).ConfigureAwait(false);
    }

    public IDisposable? SubscribeToRemovals(Action<string> onRemoved)
    {
        ArgumentNullException.ThrowIfNull(onRemoved);
        var subscriber = redis.GetSubscriber();
        void Handler(RedisChannel channel, RedisValue message)
        {
            if (message.HasValue)
            {
                onRemoved(message.ToString());
            }
        }

        subscriber.Subscribe(RemovalChannel, Handler);
        return new Subscription(subscriber, Handler);
    }

    internal static readonly RedisChannel RemovalChannel = RedisChannel.Literal("mcp:session:removed");

    private sealed class Subscription(ISubscriber subscriber, Action<RedisChannel, RedisValue> handler) : IDisposable
    {
        public void Dispose() => subscriber.Unsubscribe(RemovalChannel, handler);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(McpSessionRecord))]
internal sealed partial class McpSessionStoreJsonContext : JsonSerializerContext;
