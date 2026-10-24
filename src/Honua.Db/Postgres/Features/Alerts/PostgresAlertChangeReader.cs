// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Data;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Alerts;

/// <summary>
/// Row-visibility filter and field masks applied to a committed alert snapshot.
/// </summary>
internal interface ICommittedSnapshotPolicy
{
    /// <summary>Resolves the layer policy. A null filter and an empty mask mean the image is unchanged.</summary>
    Task<CommittedSnapshotPolicy> ResolveAsync(int layerId, CancellationToken cancellationToken);
}

/// <summary>Resolved read policy for one committed snapshot.</summary>
/// <param name="RowFilter">Permanent filter and row-level security predicate, or null.</param>
/// <param name="MaskedFields">Attribute names removed before evaluation and delivery.</param>
internal readonly record struct CommittedSnapshotPolicy(SqlFragment? RowFilter, ImmutableArray<string> MaskedFields);

/// <summary>Adapts <see cref="LayerReadSecurityResolver"/> to committed-snapshot projection.</summary>
internal sealed class LayerReadCommittedSnapshotPolicy : ICommittedSnapshotPolicy
{
    private readonly LayerReadSecurityResolver _security;

    public LayerReadCommittedSnapshotPolicy(LayerReadSecurityResolver security)
    {
        _security = security ?? throw new ArgumentNullException(nameof(security));
    }

    public async Task<CommittedSnapshotPolicy> ResolveAsync(int layerId, CancellationToken cancellationToken)
    {
        var filter = await _security.ResolveEnforcedSqlFilterAsync(layerId, cancellationToken).ConfigureAwait(false);
        var masks = await _security.ResolveMaskedFieldsAsync(layerId, cancellationToken).ConfigureAwait(false);
        return new CommittedSnapshotPolicy(filter, masks);
    }
}

internal sealed class PostgresAlertChangeReader : IAlertChangeReader, IAlertCommittedSnapshotSource
{
    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private readonly ICommittedSnapshotPolicy? _policy;

    public PostgresAlertChangeReader(IAdoNetDatabaseConnectionProvider connectionProvider)
        : this(connectionProvider, policy: null)
    {
    }

    internal PostgresAlertChangeReader(
        IAdoNetDatabaseConnectionProvider connectionProvider,
        ICommittedSnapshotPolicy? policy)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _policy = policy;
    }

    public async Task<IReadOnlyList<AlertChange>> GetChangesAfterAsync(
        long lastGeneration,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        // Metadata only. Geometry and attribute blobs are loaded later, one relevant
        // generation at a time. Versioned rows stay out of unversioned alert state.
        const string sql = """
            SELECT generation, layer_id, objectid, operation, changed_at,
                   event_id, operation_instance_id, correlation_id, audit_id, proposal_id,
                   committed_attributes IS NOT NULL
            FROM honua.feature_changes
            WHERE generation > @last_generation
              AND version_id IS NULL
            ORDER BY generation
            LIMIT @max_count
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("last_generation", NpgsqlDbType.Bigint, lastGeneration);
        command.Parameters.AddWithValue("max_count", NpgsqlDbType.Integer, maxCount);

        var changes = new List<AlertChange>(Math.Max(1, maxCount));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            changes.Add(new AlertChange
            {
                Generation = reader.GetInt64(0),
                LayerId = reader.GetInt32(1),
                ObjectId = reader.GetInt64(2),
                Operation = AlertStoreConversions.ToChangeOperation(reader.GetInt16(3)),
                ChangedAt = reader.GetFieldValue<DateTimeOffset>(4),
                SourceEventId = reader.IsDBNull(5) ? null : reader.GetString(5),
                OperationInstanceId = reader.IsDBNull(6) ? null : reader.GetString(6),
                CorrelationId = reader.IsDBNull(7) ? null : reader.GetString(7),
                AuditId = reader.IsDBNull(8) ? null : reader.GetString(8),
                ProposalId = reader.IsDBNull(9) ? null : reader.GetString(9),
                HasCommittedImage = reader.GetBoolean(10)
            });
        }

        return changes;
    }

    public async Task<Feature?> LoadProjectedSnapshotAsync(
        long generation,
        int layerId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT objectid,
                   CASE WHEN committed_geometry IS NULL THEN NULL ELSE ST_AsEWKB(committed_geometry) END,
                   committed_attributes::text,
                   committed_created_at,
                   committed_updated_at
            FROM honua.feature_changes
            WHERE generation = @generation
              AND layer_id = @layer_id
              AND version_id IS NULL
              AND committed_attributes IS NOT NULL
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        long objectId;
        byte[]? geometry;
        string attributesJson;
        DateTimeOffset? createdAt;
        DateTimeOffset? updatedAt;
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("generation", NpgsqlDbType.Bigint, generation);
            command.Parameters.AddWithValue("layer_id", NpgsqlDbType.Integer, layerId);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            objectId = reader.GetInt64(0);
            geometry = reader.IsDBNull(1) ? null : reader.GetFieldValue<byte[]>(1);
            attributesJson = reader.GetString(2);
            createdAt = reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3);
            updatedAt = reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4);
        }

        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
        FeatureAttributeJsonReader.ReadInto(attributesJson, attributes);
        // Initial creates take their id from the sequence, so the stored JSON often has no objectid.
        // The live feature reader injects the same column before rules evaluate.
        attributes["objectid"] = objectId;
        var feature = Feature.Create(objectId, geometry, attributes.ToImmutableDictionary(StringComparer.Ordinal));

        if (_policy is null)
        {
            return feature;
        }

        var policy = await _policy.ResolveAsync(layerId, cancellationToken).ConfigureAwait(false);
        if (policy.RowFilter is { } filter
            && !await SnapshotMatchesFilterAsync(connection, objectId, layerId, geometry, attributesJson, createdAt, updatedAt, filter, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ApplyFieldMasks(feature, policy.MaskedFields);
    }

    internal static Feature ApplyFieldMasks(Feature feature, ImmutableArray<string> maskedFields)
    {
        if (maskedFields.IsDefaultOrEmpty)
        {
            return feature;
        }

        var masked = maskedFields.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return feature with
        {
            Attributes = feature.Attributes.RemoveRange(feature.Attributes.Keys.Where(masked.Contains))
        };
    }

    internal static async Task<bool> SnapshotMatchesFilterAsync(
        NpgsqlConnection connection,
        long objectId,
        int layerId,
        byte[]? geometry,
        string attributesJson,
        DateTimeOffset? createdAt,
        DateTimeOffset? updatedAt,
        SqlFragment filter,
        CancellationToken cancellationToken)
    {
        var paramIndex = 7;
        var predicate = FeatureQueryBuilder.ConvertNamedParametersToPositional(filter.Sql, ref paramIndex);
        var sql = $"""
            SELECT EXISTS (
                SELECT 1
                FROM (
                    SELECT
                        $1::bigint AS objectid,
                        $2::integer AS layer_id,
                        CASE WHEN $3 IS NULL THEN NULL ELSE ST_GeomFromEWKB($3) END AS geometry,
                        $4::jsonb AS attributes,
                        $5::timestamptz AS created_at,
                        $6::timestamptz AS updated_at
                ) AS snapshot
                WHERE ({predicate})
            )
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = objectId });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = layerId });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = geometry ?? (object)DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = string.IsNullOrWhiteSpace(attributesJson) ? "{}" : attributesJson });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = createdAt ?? (object)DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = updatedAt ?? (object)DBNull.Value });
        foreach (var parameter in filter.Parameters)
        {
            command.Parameters.AddWithValue(parameter ?? DBNull.Value);
        }

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is bool visible && visible;
    }
}
