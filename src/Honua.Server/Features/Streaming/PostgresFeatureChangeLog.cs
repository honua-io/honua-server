// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.ControlPlane;
using Honua.Core.Features.Realtime;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.Infrastructure.Abstractions;
using Honua.Infrastructure.Events;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Server.Features.Streaming;

// Feature changes as a realtime family (specification realtime-cross-node-rc3 sections 3.1 and 5.3).
// The change-tracking trigger writes honua.feature_changes in the edit transaction, so every edit
// is here whichever task, or SQL session, made it. Positions are generations: increasing, with gaps
// (rollbacks, branch rows), so "after N" never means "the Nth". Only DEFAULT-version rows are events.
internal sealed class PostgresFeatureChangeLog(
    JobStoreDatabase database,
    RealtimeCapabilityLimits limits,
    TimeProvider timeProvider) : IRealtimeFamilySource
{
    public const string DatabaseProtocol = "database";

    private static readonly TimeSpan _windowCacheLifetime = TimeSpan.FromSeconds(30);

    // The change-tracking trigger writes the change log into the honua schema whatever
    // Database:Schema names, so these names do not follow the job-store schema.
    private const string ChangesTable = "honua.feature_changes";
    private const string OutboxTable = "honua.feature_change_outbox";

    // The committed horizon (migrations 213 and 228). Writers commit out of generation order, so
    // cursor reads stop at the published prefix and never pass an uncommitted lower generation.
    private const string ReadHorizon = "honua.feature_change_read_horizon()";
    private readonly Lock _windowGate = new();
    private (long Head, long Oldest, DateTimeOffset ReadAt)? _cachedOldest;

    public RealtimeFamily Family => RealtimeFamily.Features;

    // The event id a change without an outbox row carries: the change made directly in the database.
    public static string DatabaseEventId(long generation)
        => new RealtimePosition(RealtimeFamily.Features, generation).ToString();

    // Each read publishes the committed prefix once and returns it. Commits that were in flight
    // are published by the read their own NOTIFY wakes, so the head needs no background publisher.
    public Task<long> ReadHeadAsync(CancellationToken cancellationToken)
        => database.ExecuteAsync(
            async (connection, _, token) =>
            {
                _ = await FeatureChangePrefix.PublishAsync(connection, token).ConfigureAwait(false);
                await using var command = new NpgsqlCommand($"SELECT {ReadHorizon}", connection);
                return (long)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!;
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);

    public async Task<IReadOnlyList<RealtimeEvent>> ReadAfterAsync(
        long after,
        IReadOnlyCollection<string>? scopeKeys,
        int limit,
        CancellationToken cancellationToken)
    {
        var layers = scopeKeys is { Count: > 0 }
            ? scopeKeys.Select(key => int.Parse(key, NumberStyles.Integer, CultureInfo.InvariantCulture)).ToArray()
            : null;
        var changes = await ReadAsync(new FeatureChangeLogQuery(after, null, null, layers, limit), cancellationToken).ConfigureAwait(false);
        return changes.Select(ToRealtimeEvent).ToArray();
    }

    // Changes after query.After in generation order, optionally bounded by change time and layers.
    public Task<IReadOnlyList<FeatureChangeEvent>> ReadAsync(FeatureChangeLogQuery query, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Limit, 1);
        return database.ExecuteAsync<IReadOnlyList<FeatureChangeEvent>>(
            async (connection, _, token) =>
            {
                if (query.EventId is null)
                {
                    // A cursor read stops at the horizon; publish first so commits it can see count.
                    _ = await FeatureChangePrefix.PublishAsync(connection, token).ConfigureAwait(false);
                }

                await using var command = new NpgsqlCommand(SelectChanges(query), connection);
                command.Parameters.Add(new NpgsqlParameter("after", NpgsqlDbType.Bigint) { Value = query.After });
                command.Parameters.Add(new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = query.Limit });
                AddOptional(command, "from", NpgsqlDbType.TimestampTz, query.From);
                AddOptional(command, "to", NpgsqlDbType.TimestampTz, query.To);
                if (query.EventId is not null)
                {
                    command.Parameters.Add(new NpgsqlParameter("event_id", NpgsqlDbType.Text) { Value = query.EventId });
                }

                if (query.LayerIds is { Count: > 0 } layerIds)
                {
                    command.Parameters.Add(new NpgsqlParameter("layers", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = layerIds.ToArray() });
                }

                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                var changes = new List<FeatureChangeEvent>();
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    changes.Add(FeatureChangeLogRow.Read(reader).ToEvent());
                }

                return changes;
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);
    }

    // The DEFAULT-version change an outbox-backed edit wrote under eventId, or null.
    public async Task<FeatureChangeEvent?> ReadByEventIdAsync(string eventId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        var changes = await ReadAsync(new FeatureChangeLogQuery(0, null, null, null, 1) { EventId = eventId }, cancellationToken)
            .ConfigureAwait(false);
        return changes.Count == 0 ? null : changes[0];
    }

    // Oldest is the first position still resumable: every change after Oldest - 1 is within both the
    // age limit (Realtime:Retention:features) and the count limit (Realtime:ResumeMaxEvents), and was
    // not pruned. It is the boundary after the newest change outside the window, not the oldest
    // change inside it, because a generation gap before that change loses nothing. Cached for 30 s
    // only while the head is unchanged; new generations can advance the count boundary immediately.
    public async Task<RealtimeLogWindow> ReadWindowAsync(CancellationToken cancellationToken)
    {
        var head = await ReadHeadAsync(cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        lock (_windowGate)
        {
            if (_cachedOldest is { } cached && cached.Head == head && now - cached.ReadAt < _windowCacheLifetime)
            {
                return new RealtimeLogWindow(Family, Math.Min(cached.Oldest, head + 1), head);
            }
        }

        var oldest = await ReadOldestResumableAsync(head, now, cancellationToken).ConfigureAwait(false);
        lock (_windowGate)
        {
            _cachedOldest = (head, oldest, now);
        }

        return new RealtimeLogWindow(Family, oldest, head);
    }

    private Task<long> ReadOldestResumableAsync(long head, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var retention = limits.FeatureRetention;
        return database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    SELECT
                        (SELECT min(generation) FROM {ChangesTable} WHERE generation <= @head),
                        (SELECT generation FROM {ChangesTable} WHERE version_id IS NULL AND generation <= @head
                         ORDER BY generation DESC OFFSET @max_events LIMIT 1),
                        (SELECT max(generation) FROM {ChangesTable}
                         WHERE version_id IS NULL AND generation <= @head AND changed_at < @since)
                    """,
                    connection);
                // The window is measured against the published head, never past it, so a generation
                // the horizon still holds back cannot push a published one out of the resumable window.
                command.Parameters.Add(new NpgsqlParameter("head", NpgsqlDbType.Bigint) { Value = head });
                command.Parameters.Add(new NpgsqlParameter("max_events", NpgsqlDbType.Integer) { Value = limits.CursorRetentionLimit });
                command.Parameters.Add(new NpgsqlParameter("since", NpgsqlDbType.TimestampTz) { Value = now - retention });
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                await reader.ReadAsync(token).ConfigureAwait(false);
                if (reader.IsDBNull(0))
                {
                    return head + 1;
                }

                var first = reader.GetInt64(0);
                var afterCountLimit = reader.IsDBNull(1) ? first : reader.GetInt64(1) + 1;
                var afterAgeLimit = reader.IsDBNull(2) ? first : reader.GetInt64(2) + 1;
                return Math.Max(first, Math.Max(afterCountLimit, afterAgeLimit));
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);
    }

    private static RealtimeEvent ToRealtimeEvent(FeatureChangeEvent change)
        => new(
            RealtimeFamily.Features,
            change.Cursor,
            change.LayerId.ToString(CultureInfo.InvariantCulture),
            change.Operation,
            JsonSerializer.SerializeToElement(change, FeatureChangeEventsJsonContext.Default.FeatureChangeEvent),
            change.Timestamp);

    private static string SelectChanges(FeatureChangeLogQuery query)
    {
        return $"""
            SELECT changes.generation, changes.layer_id, COALESCE(changes.public_objectid, changes.objectid), changes.operation, changes.changed_at,
                   changes.event_id, changes.operation_instance_id, changes.correlation_id, changes.audit_id,
                   changes.proposal_id, COALESCE(changes.source_id, outbox.source_id),
                   outbox.service_id, outbox.protocol, outbox.request_id,
                   changes.pre_attributes::text, changes.committed_attributes::text,
                   ST_AsEWKB(CASE WHEN changes.operation = 3 THEN changes.pre_geometry ELSE changes.committed_geometry END),
                   changes.operation = 2
                       AND ST_AsEWKB(changes.pre_geometry) IS DISTINCT FROM ST_AsEWKB(changes.committed_geometry)
            FROM {ChangesTable} AS changes
            LEFT JOIN LATERAL (
                SELECT entry.service_id, entry.protocol, entry.request_id, entry.source_id
                FROM {OutboxTable} AS entry WHERE entry.event_id = changes.event_id LIMIT 1) AS outbox ON true
            WHERE {BuildFilters(query)}
            ORDER BY changes.generation
            LIMIT @limit
            """;
    }

    private static string BuildFilters(FeatureChangeLogQuery query)
    {
        var filters = new List<string> { "changes.generation > @after", "changes.version_id IS NULL" };
        if (query.EventId is null)
        {
            // An event-id lookup advances no cursor, so only cursor reads stop at the horizon.
            filters.Add($"changes.generation <= {ReadHorizon}");
        }

        if (query.From is not null)
        {
            filters.Add("changes.changed_at >= @from");
        }

        if (query.To is not null)
        {
            filters.Add("changes.changed_at <= @to");
        }

        if (query.LayerIds is { Count: > 0 })
        {
            filters.Add("changes.layer_id = ANY(@layers)");
        }

        if (query.EventId is not null)
        {
            filters.Add("changes.event_id = @event_id");
        }

        return string.Join(" AND ", filters);
    }

    private static void AddOptional(NpgsqlCommand command, string name, NpgsqlDbType type, DateTimeOffset? value)
    {
        if (value is { } present)
        {
            command.Parameters.Add(new NpgsqlParameter(name, type) { Value = present });
        }
    }
}

// What a feature-change read selects: changes after a generation, optionally by change time and layers.
internal readonly record struct FeatureChangeLogQuery(
    long After,
    DateTimeOffset? From,
    DateTimeOffset? To,
    IReadOnlyCollection<int>? LayerIds,
    int Limit)
{
    public string? EventId { get; init; }
}
