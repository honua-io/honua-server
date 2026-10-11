// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using StackExchange.Redis;

namespace Honua.Ai.Protocols.Mcp;

/// <summary>
/// The serializable part of an MCP Streamable-HTTP session: everything a server
/// instance needs to honour a session it did not mint. The per-session
/// notification channel, lifetime token, and job index are deliberately absent —
/// they back an SSE stream held open on one node and stay node-local.
/// </summary>
/// <param name="SessionId">The <c>Mcp-Session-Id</c> minted at <c>initialize</c>.</param>
/// <param name="PrincipalKey">
/// Stable binding key of the principal that ran <c>initialize</c>
/// (<see cref="McpSessionManager.AnonymousPrincipalKey"/> for an anonymous session).
/// </param>
/// <param name="ElicitationSupported">Whether the client advertised elicitation at <c>initialize</c>.</param>
/// <param name="WorkflowView">The workflow discovery view negotiated at <c>initialize</c>, or <c>null</c>.</param>
/// <param name="CreatedUtc">When the session was minted.</param>
/// <param name="SchemaVersion">Record schema version, so a later shape change can be detected.</param>
internal sealed record McpSessionRecord(
    string SessionId,
    string PrincipalKey,
    bool ElicitationSupported,
    string? WorkflowView,
    DateTimeOffset CreatedUtc,
    int SchemaVersion = McpSessionRecord.CurrentSchemaVersion)
{
    /// <summary>The schema version written by this build.</summary>
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// Shared, cross-instance store for MCP session records. When one is composed
/// (Redis connected), a session negotiated on any instance is honoured by every
/// instance: the in-memory <see cref="McpSessionManager"/> becomes a read-through
/// cache in front of it. The store is authoritative for existence — the sliding
/// idle window lives in the record's expiry, and <c>DELETE /mcp</c> on any
/// instance removes it for all of them.
/// </summary>
internal interface IMcpSessionStore
{
    /// <summary>Persists a freshly minted session with the idle TTL as its expiry.</summary>
    Task SaveAsync(McpSessionRecord record, TimeSpan idleTimeout, CancellationToken cancellationToken);

    /// <summary>
    /// Reads a session and slides its expiry to <paramref name="idleTimeout"/> from
    /// now. Returns <c>null</c> when the session is unknown, terminated, or expired.
    /// </summary>
    Task<McpSessionRecord?> GetAndTouchAsync(string sessionId, TimeSpan idleTimeout, CancellationToken cancellationToken);

    /// <summary>
    /// Reads a session without sliding its expiry. Returns <c>null</c> when the
    /// session is unknown, terminated, or expired. Callers slide the expiry only
    /// after the presented principal matches the record.
    /// </summary>
    Task<McpSessionRecord?> GetAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Slides an existing session's expiry. Returns <c>false</c> when the session no
    /// longer exists (terminated or expired on another instance).
    /// </summary>
    Task<bool> TouchAsync(string sessionId, TimeSpan idleTimeout, CancellationToken cancellationToken);

    /// <summary>
    /// Returns whether the session record exists, without sliding its expiry (used
    /// for callers that are not the session's principal).
    /// </summary>
    Task<bool> ExistsAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Removes a session so every instance answers it with HTTP 404, and broadcasts
    /// the removal to subscribers (see <see cref="SubscribeToRemovals"/>).
    /// </summary>
    Task RemoveAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Subscribes to session removals made by any instance, so a replica holding
    /// node-local session work (an SSE stream, progress bridges) ends it at once.
    /// Delivery is best effort; every request still confirms existence against the
    /// store, so a missed broadcast only delays local teardown.
    /// </summary>
    IDisposable? SubscribeToRemovals(Action<string> onRemoved);
}

/// <summary>
/// Redis-backed <see cref="IMcpSessionStore"/>. Each session is one string key
/// holding the source-generated JSON record, with the MCP idle timeout as its
/// TTL; every accepted request slides the TTL, so the key's expiry is the
/// session's last-seen time plus the idle window.
/// </summary>
internal sealed class RedisMcpSessionStore(IConnectionMultiplexer redis) : IMcpSessionStore
{
    /// <summary>Key prefix for MCP session records.</summary>
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

    /// <summary>Pub/sub channel carrying ids of sessions removed by any instance.</summary>
    internal static readonly RedisChannel RemovalChannel = RedisChannel.Literal("mcp:session:removed");

    private sealed class Subscription(ISubscriber subscriber, Action<RedisChannel, RedisValue> handler) : IDisposable
    {
        public void Dispose() => subscriber.Unsubscribe(RemovalChannel, handler);
    }
}

/// <summary>
/// Source-generated (AOT-safe, reflection-free) serializer context for
/// <see cref="McpSessionRecord"/>.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(McpSessionRecord))]
internal sealed partial class McpSessionStoreJsonContext : JsonSerializerContext;
