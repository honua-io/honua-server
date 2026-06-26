// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using StackExchange.Redis;

namespace Honua.Protocols.Ogc.Classic.Wfs20.Services;

/// <summary>
/// Redis-backed durable store for managed WFS 2.0 stored queries. Registered in place
/// of <see cref="InMemoryWfsStoredQueryStore"/> when Redis shared state is configured,
/// so CreateStoredQuery/DropStoredQuery results are visible on every replica and
/// survive process restarts.
/// </summary>
/// <remarks>
/// Definitions live in a single Redis hash keyed by the lower-cased stored query id
/// (preserving the case-insensitive identifier semantics of the in-memory store) with
/// AOT-safe source-generated JSON payloads. Stored queries are explicitly managed
/// resources, so entries carry no expiry: they persist until dropped.
/// </remarks>
internal sealed partial class RedisWfsStoredQueryStore(
    IConnectionMultiplexer redis,
    ILogger<RedisWfsStoredQueryStore> logger) : IWfsStoredQueryStore
{
    private const string HashKey = "wfs:storedquery:managed";

    private readonly IDatabase _database = redis.GetDatabase();

    public async Task<WfsStoredQueryDefinition?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        cancellationToken.ThrowIfCancellationRequested();

        var payload = await _database.HashGetAsync(HashKey, GetField(id)).ConfigureAwait(false);
        return payload.HasValue ? Deserialize(payload.ToString()) : null;
    }

    public async Task<IReadOnlyList<WfsStoredQueryDefinition>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var entries = await _database.HashGetAllAsync(HashKey).ConfigureAwait(false);
        if (entries.Length == 0)
        {
            return Array.Empty<WfsStoredQueryDefinition>();
        }

        var results = new List<WfsStoredQueryDefinition>(entries.Length);
        foreach (var entry in entries)
        {
            if (!entry.Value.HasValue)
            {
                continue;
            }

            var definition = Deserialize(entry.Value.ToString());
            if (definition is not null)
            {
                results.Add(definition);
            }
        }

        return results
            .OrderBy(query => query.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var length = await _database.HashLengthAsync(HashKey).ConfigureAwait(false);
        return (int)Math.Min(length, int.MaxValue);
    }

    public async Task<WfsStoredQueryCreateStatus> TryCreateAsync(
        WfsStoredQueryDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        cancellationToken.ThrowIfCancellationRequested();

        var field = GetField(definition.Id);
        var payload = JsonSerializer.Serialize(definition, OgcClassicJsonContext.Default.WfsStoredQueryDefinition);

        // MULTI/EXEC with HEXISTS + HLEN guards makes duplicate detection and the
        // capacity cap atomic across replicas; competing creates settle in the loop.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var transaction = _database.CreateTransaction();
            transaction.AddCondition(Condition.HashNotExists(HashKey, field));
            transaction.AddCondition(Condition.HashLengthLessThan(HashKey, IWfsStoredQueryStore.MaxManagedStoredQueryCount));
            _ = transaction.HashSetAsync(HashKey, field, payload);
            if (await transaction.ExecuteAsync().ConfigureAwait(false))
            {
                Log.StoredQueryCreated(logger, definition.Id);
                return WfsStoredQueryCreateStatus.Created;
            }

            // The transaction conditions failed; classify which guard rejected the write.
            if (await _database.HashExistsAsync(HashKey, field).ConfigureAwait(false))
            {
                return WfsStoredQueryCreateStatus.DuplicateId;
            }

            var length = await _database.HashLengthAsync(HashKey).ConfigureAwait(false);
            if (length >= IWfsStoredQueryStore.MaxManagedStoredQueryCount)
            {
                return WfsStoredQueryCreateStatus.CapacityExceeded;
            }

            // A concurrent mutation invalidated the conditions between EXEC and the
            // classification reads (e.g. a competing entry was dropped); retry.
        }

        // Persistent contention without a classifiable cause — surface as capacity
        // pressure so the client receives the documented OperationProcessingFailed.
        Log.StoredQueryCreateContended(logger, definition.Id);
        return WfsStoredQueryCreateStatus.CapacityExceeded;
    }

    public async Task<bool> TryRemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        cancellationToken.ThrowIfCancellationRequested();

        var removed = await _database.HashDeleteAsync(HashKey, GetField(id)).ConfigureAwait(false);
        if (removed)
        {
            Log.StoredQueryDropped(logger, id);
        }

        return removed;
    }

    private WfsStoredQueryDefinition? Deserialize(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize(payload, OgcClassicJsonContext.Default.WfsStoredQueryDefinition);
        }
        catch (JsonException ex)
        {
            // A corrupt entry must not take down ListStoredQueries/DescribeStoredQueries
            // for the healthy definitions; skip it and leave a diagnostic trail.
            Log.StoredQueryPayloadCorrupt(logger, ex);
            return null;
        }
    }

    private static string GetField(string id) => id.Trim().ToLowerInvariant();

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "WFS 2.0 managed stored query '{StoredQueryId}' persisted to Redis")]
        public static partial void StoredQueryCreated(ILogger logger, string storedQueryId);

        [LoggerMessage(Level = LogLevel.Information, Message = "WFS 2.0 managed stored query '{StoredQueryId}' removed from Redis")]
        public static partial void StoredQueryDropped(ILogger logger, string storedQueryId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "WFS 2.0 CreateStoredQuery for '{StoredQueryId}' exhausted optimistic retries under concurrent mutation")]
        public static partial void StoredQueryCreateContended(ILogger logger, string storedQueryId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping corrupt WFS 2.0 managed stored query payload in Redis")]
        public static partial void StoredQueryPayloadCorrupt(ILogger logger, Exception exception);
    }
}
