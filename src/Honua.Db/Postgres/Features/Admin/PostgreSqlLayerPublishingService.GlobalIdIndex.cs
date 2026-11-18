// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Admin.Domain;
using Honua.Core.Queries.Filters;
using Npgsql;

namespace Honua.Db.Postgres.Features.Admin;

internal sealed partial class PostgreSqlLayerPublishingService
{
    private sealed record ManagedGlobalIdIndex
    {
        public required string FeaturesSchema { get; init; }

        public required string FeaturesTable { get; init; }

        public required int LayerId { get; init; }

        public required string Field { get; init; }

        public string QualifiedTable =>
            $"{SqlIdentifier.QuoteIdentifier(FeaturesSchema)}.{SqlIdentifier.QuoteIdentifier(FeaturesTable)}";
    }

    // Null when the publish is not managed or declares no global-ID field.
    private static ManagedGlobalIdIndex? ManagedGlobalIdIndexFor(PublishedLayerStorage storage, int layerId, string? field) =>
        storage.IsManagedStore && field is not null
            ? new ManagedGlobalIdIndex
            {
                FeaturesSchema = storage.Schema,
                FeaturesTable = storage.Table,
                LayerId = layerId,
                Field = field
            }
            : null;

    // Two such publishes would each insert rows (RowExclusiveLock) and then wait for the other's rows
    // to build their index (ShareLock). Taken before materializing rows, this lock orders them instead.
    // The activation function never takes it, so the graph save cannot wait on it.
    private static async Task SerializeGlobalIdPublishAsync(
        NpgsqlTransaction transaction,
        ManagedGlobalIdIndex? index,
        CancellationToken cancellationToken)
    {
        if (index is null)
        {
            return;
        }

        const string sql = "SELECT pg_advisory_xact_lock(hashtextextended('global-id-publish-serial', 0))";
        await using var command = new NpgsqlCommand(sql, transaction.Connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // A managed publish that declares a global-ID field builds the layer's identity index inside the
    // publishing transaction, which already holds RowExclusiveLock on the feature table. The graph
    // activation runs on another connection and skips a layer whose publisher holds this advisory lock
    // (migration 219); without it the activation waits for a ShareLock this transaction never releases.
    // Name, expression and predicate match sync_metadata_global_id_indexes and the managed reader check.
    private static async Task BuildManagedGlobalIdIndexAsync(
        NpgsqlTransaction transaction,
        ManagedGlobalIdIndex? index,
        CancellationToken cancellationToken)
    {
        if (index is null)
        {
            return;
        }

        var indexName = await LockPublishingLayerAsync(transaction, index, cancellationToken).ConfigureAwait(false);
        var fieldLiteral = "'" + index.Field.Replace("'", "''", StringComparison.Ordinal) + "'";
        var indexSql =
            $"CREATE UNIQUE INDEX IF NOT EXISTS {SqlIdentifier.QuoteIdentifier(indexName)} ON {index.QualifiedTable} " +
            $"((NULLIF(attributes->>{fieldLiteral}, '')::uuid)) WHERE layer_id = {index.LayerId}";
        try
        {
            await using var command = new NpgsqlCommand(indexSql, transaction.Connection, transaction);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UniqueViolation
            or PostgresErrorCodes.InvalidTextRepresentation)
        {
            throw new LayerPublishingException(
                LayerPublishingErrorKind.Validation,
                $"GlobalIdField '{index.Field}' must hold a distinct UUID or no value in every row; " +
                "the source has duplicate or non-UUID values. Repair them before publishing.");
        }
    }

    // Takes the per-layer publish lock until commit and returns the identity index name.
    private static async Task<string> LockPublishingLayerAsync(
        NpgsqlTransaction transaction,
        ManagedGlobalIdIndex index,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT 'idx_features_gid_' || md5(@layerId::text || ':' || @field)
            FROM (SELECT pg_advisory_xact_lock(hashtextextended(
                'global-id-publish:' || to_regclass(@table)::oid::text || ':' || @layerId::text, 0))) publish_lock;
            """;
        await using var command = new NpgsqlCommand(sql, transaction.Connection, transaction);
        command.Parameters.AddWithValue("@table", index.QualifiedTable);
        command.Parameters.AddWithValue("@layerId", index.LayerId);
        command.Parameters.AddWithValue("@field", index.Field);
        return (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }
}
