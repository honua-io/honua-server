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
    /// Slides an existing session's expiry. Returns <c>false</c> when the session no
    /// longer exists (terminated or expired on another instance).
    /// </summary>
    Task<bool> TouchAsync(string sessionId, TimeSpan idleTimeout, CancellationToken cancellationToken);

    /// <summary>Removes a session so every instance answers it with HTTP 404.</summary>
    Task RemoveAsync(string sessionId, CancellationToken cancellationToken);
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

    public async Task RemoveAsync(string sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await redis.GetDatabase().KeyDeleteAsync(Key(sessionId)).ConfigureAwait(false);
    }
}

/// <summary>
/// Source-generated (AOT-safe, reflection-free) serializer context for
/// <see cref="McpSessionRecord"/>.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(McpSessionRecord))]
internal sealed partial class McpSessionStoreJsonContext : JsonSerializerContext;
