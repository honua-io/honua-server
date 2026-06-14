// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Temporal.Abstractions;
using Honua.Core.Features.Temporal.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.FeatureStore.Services;

/// <summary>
/// Reads the uncollapsed change log with attribution from Postgres for the temporal diff, timeline, and
/// rollback surfaces (slices 2-5 of honua-server#1166). Reads <c>honua.feature_changes</c> directly,
/// including the slice-4 attribution columns and the named-checkpoint registry.
/// </summary>
internal sealed class PostgresTemporalHistoryStore : ITemporalHistoryStore
{
    private readonly IDatabaseConnectionProvider _connectionProvider;

    public PostgresTemporalHistoryStore(IDatabaseConnectionProvider connectionProvider)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
    }

    public async Task<IReadOnlyList<TemporalChangeRecord>> GetFeatureRevisionsAsync(
        int storageLayerId,
        long objectId,
        long afterGeneration,
        int limit,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT generation, objectid, operation, changed_at, actor, source, operation_name, source_id
            FROM honua.feature_changes
            WHERE layer_id = $1 AND objectid = $2 AND generation > $3
            ORDER BY generation ASC
            LIMIT $4
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, storageLayerId);
        command.Parameters.AddWithValue(NpgsqlDbType.Bigint, objectId);
        command.Parameters.AddWithValue(NpgsqlDbType.Bigint, afterGeneration);
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, limit);

        return await ReadRecordsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TemporalChangeRecord>> GetChangesInWindowAsync(
        int storageLayerId,
        long fromGeneration,
        long toGeneration,
        long afterObjectId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        // Page by distinct object id: select rows for the first <limit> object ids greater than the cursor
        // within the window, preserving every revision per feature so the caller can collapse them.
        const string sql = """
            WITH ids AS (
                SELECT DISTINCT objectid
                FROM honua.feature_changes
                WHERE layer_id = $1 AND generation > $2 AND generation <= $3 AND objectid > $4
                ORDER BY objectid ASC
                LIMIT $5
            )
            SELECT fc.generation, fc.objectid, fc.operation, fc.changed_at,
                   fc.actor, fc.source, fc.operation_name, fc.source_id
            FROM honua.feature_changes fc
            JOIN ids ON ids.objectid = fc.objectid
            WHERE fc.layer_id = $1 AND fc.generation > $2 AND fc.generation <= $3
            ORDER BY fc.objectid ASC, fc.generation ASC
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, storageLayerId);
        command.Parameters.AddWithValue(NpgsqlDbType.Bigint, fromGeneration);
        command.Parameters.AddWithValue(NpgsqlDbType.Bigint, toGeneration);
        command.Parameters.AddWithValue(NpgsqlDbType.Bigint, afterObjectId);
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, limit);

        return await ReadRecordsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> CountChangedFeaturesAsync(
        int storageLayerId,
        long fromGeneration,
        long toGeneration,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT COUNT(DISTINCT objectid)
            FROM honua.feature_changes
            WHERE layer_id = $1 AND generation > $2 AND generation <= $3
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, storageLayerId);
        command.Parameters.AddWithValue(NpgsqlDbType.Bigint, fromGeneration);
        command.Parameters.AddWithValue(NpgsqlDbType.Bigint, toGeneration);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is long count ? (int)count : 0;
    }

    public async Task<long?> ResolveCheckpointGenerationAsync(
        int storageLayerId,
        TemporalCheckpointKind kind,
        string value,
        long upperBoundGeneration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);

        switch (kind)
        {
            case TemporalCheckpointKind.Timestamp:
                {
                    if (!DateTimeOffset.TryParse(
                            value,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                            out var timestamp))
                    {
                        return null;
                    }

                    const string sql = """
                    SELECT MAX(generation)
                    FROM honua.feature_changes
                    WHERE layer_id = $1 AND changed_at <= $2 AND generation <= $3
                    """;
                    await using var command = new NpgsqlCommand(sql, connection);
                    command.Parameters.AddWithValue(NpgsqlDbType.Integer, storageLayerId);
                    command.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, timestamp.ToUniversalTime());
                    command.Parameters.AddWithValue(NpgsqlDbType.Bigint, upperBoundGeneration);
                    var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                    // No row at or before the timestamp resolves to generation 0 (start of history).
                    return result is long generation ? generation : 0L;
                }

            case TemporalCheckpointKind.Release:
            case TemporalCheckpointKind.Job:
            case TemporalCheckpointKind.EditSession:
            case TemporalCheckpointKind.Transaction:
                {
                    // These all resolve through the attribution source_id recorded alongside the change.
                    const string sql = """
                    SELECT MAX(generation)
                    FROM honua.feature_changes
                    WHERE layer_id = $1 AND source_id = $2 AND generation <= $3
                    """;
                    await using var command = new NpgsqlCommand(sql, connection);
                    command.Parameters.AddWithValue(NpgsqlDbType.Integer, storageLayerId);
                    command.Parameters.AddWithValue(NpgsqlDbType.Text, value);
                    command.Parameters.AddWithValue(NpgsqlDbType.Bigint, upperBoundGeneration);
                    var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                    return result is long generation ? generation : (long?)null;
                }

            case TemporalCheckpointKind.Named:
                {
                    const string sql = """
                    SELECT generation
                    FROM honua.temporal_checkpoints
                    WHERE layer_id = $1 AND name = $2
                    """;
                    await using var command = new NpgsqlCommand(sql, connection);
                    command.Parameters.AddWithValue(NpgsqlDbType.Integer, storageLayerId);
                    command.Parameters.AddWithValue(NpgsqlDbType.Text, value);
                    var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                    return result is long generation ? Math.Min(generation, upperBoundGeneration) : (long?)null;
                }

            default:
                return null;
        }
    }

    private static async Task<IReadOnlyList<TemporalChangeRecord>> ReadRecordsAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        var records = new List<TemporalChangeRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var generation = reader.GetInt64(0);
            var objectId = reader.GetInt64(1);
            var operation = (TemporalChangeKind)reader.GetInt16(2);
            var changedAt = reader.GetFieldValue<DateTimeOffset>(3);

            var actor = await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false)
                ? null
                : reader.GetString(4);
            var source = await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false)
                ? TemporalAttributionSource.Unknown
                : (TemporalAttributionSource)reader.GetInt16(5);
            var operationName = await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false)
                ? null
                : reader.GetString(6);
            var sourceId = await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false)
                ? null
                : reader.GetString(7);

            TemporalAttribution? attribution = null;
            if (actor is not null || source != TemporalAttributionSource.Unknown
                || operationName is not null || sourceId is not null)
            {
                attribution = new TemporalAttribution(actor, source, operationName, sourceId);
            }

            records.Add(new TemporalChangeRecord(
                Generation: generation,
                ObjectId: objectId,
                Operation: operation,
                ChangedAtUtc: changedAt.ToUniversalTime(),
                Attribution: attribution));
        }

        return records;
    }
}
