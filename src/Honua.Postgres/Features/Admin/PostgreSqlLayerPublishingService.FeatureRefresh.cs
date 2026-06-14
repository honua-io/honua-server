// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.
//
// Partial: canonical feature snapshot refresh.
//
// Rebuilds a published layer's rows in the canonical `features` table from its live source
// table. Publishing materializes a one-time snapshot (MaterializeLayerFeaturesAsync) that the
// server-side MVT/tile path reads, while the feature-query paths read the live source table.
// Re-importing or updating the source therefore leaves the snapshot — and the tiles built from
// it — silently stale (honua-server#1628). These helpers delete the layer's stale snapshot rows
// and re-insert them from the live source within a single transaction so tiles stay consistent
// with the live data the query paths serve.

using System.Globalization;
using Honua.Core.Features.Admin.Domain;
using Npgsql;

namespace Honua.Postgres.Features.Admin;

internal sealed partial class PostgreSqlLayerPublishingService
{
    /// <inheritdoc/>
    public async Task<MaterializedFeatureRefreshResult?> RefreshMaterializedFeaturesAsync(
        string connectionString,
        int layerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var metadata = await ResolveMaterializeRefreshMetadataByIdAsync(
                connectionString,
                layerId,
                cancellationToken)
            .ConfigureAwait(false);
        if (metadata == null)
        {
            return null;
        }

        return await RebuildLayerSnapshotAsync(connectionString, metadata, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MaterializedFeatureRefreshResult>> RefreshMaterializedFeaturesForSourceTableAsync(
        string connectionString,
        string? schema,
        string table,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);

        var layerMetadata = await ResolveMaterializeRefreshMetadataBySourceAsync(
                connectionString,
                schema,
                table,
                cancellationToken)
            .ConfigureAwait(false);
        if (layerMetadata.Count == 0)
        {
            return [];
        }

        var results = new List<MaterializedFeatureRefreshResult>(layerMetadata.Count);
        foreach (var metadata in layerMetadata)
        {
            var result = await RebuildLayerSnapshotAsync(connectionString, metadata, cancellationToken)
                .ConfigureAwait(false);
            if (result != null)
            {
                results.Add(result);
            }
        }

        return results;
    }

    private async Task<MaterializedFeatureRefreshResult?> RebuildLayerSnapshotAsync(
        string connectionString,
        MaterializeRefreshMetadata metadata,
        CancellationToken cancellationToken)
    {
        // Re-introspect the live source table so the rebuilt snapshot picks up any source
        // schema changes and so the projected attributes match what the layer published.
        var tableInfo = await ResolveTableInfoAsync(
                connectionString,
                metadata.Schema,
                metadata.Table,
                cancellationToken)
            .ConfigureAwait(false);
        if (tableInfo == null)
        {
            // Source table is gone; leave the existing snapshot untouched rather than
            // wiping the canonical rows the tile path still serves.
            return null;
        }

        var attributeColumns = SelectMaterializedAttributeColumns(
            tableInfo,
            metadata.FieldNames,
            metadata.GeometryColumn);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await DeleteLayerFeaturesAsync(connection, transaction, metadata.LayerId, cancellationToken)
            .ConfigureAwait(false);

        var materializedCount = await MaterializeLayerFeaturesAsync(
                connection,
                transaction,
                metadata.LayerId,
                metadata.Schema,
                metadata.Table,
                metadata.GeometryColumn,
                metadata.Srid,
                attributeColumns,
                cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        Log.LayerSnapshotRefreshed(_logger, metadata.LayerId, materializedCount);

        return new MaterializedFeatureRefreshResult
        {
            LayerId = metadata.LayerId,
            LayerName = metadata.LayerName,
            Schema = metadata.Schema,
            Table = metadata.Table,
            MaterializedFeatureCount = materializedCount
        };
    }

    private static List<ColumnInfo> SelectMaterializedAttributeColumns(
        TableInfo tableInfo,
        IReadOnlyList<string> publishedFieldNames,
        string geometryColumn)
    {
        // Mirror the publish-time projection: every published, non-geometry source column is
        // folded into the canonical `attributes` JSONB (the primary key is included too, exactly
        // as MaterializeLayerFeaturesAsync does at publish). Order by the layer's field order so
        // the rebuilt JSONB matches the original snapshot shape.
        var sourceColumns = tableInfo.Columns.ToDictionary(
            column => column.Name,
            StringComparer.OrdinalIgnoreCase);

        var selected = new List<ColumnInfo>(publishedFieldNames.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fieldName in publishedFieldNames)
        {
            if (string.Equals(fieldName, geometryColumn, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (sourceColumns.TryGetValue(fieldName, out var column) && seen.Add(column.Name))
            {
                selected.Add(column);
            }
        }

        return selected;
    }

    private static async Task DeleteLayerFeaturesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int layerId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE FROM features
            WHERE layer_id = @layerId;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@layerId", layerId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<MaterializeRefreshMetadata?> ResolveMaterializeRefreshMetadataByIdAsync(
        string connectionString,
        int layerId,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        const string sql = """
            SELECT
                layer_id,
                layer_name,
                table_schema,
                table_name,
                geometry_column,
                COALESCE(NULLIF(srid, 0), NULLIF(storage_srid, 0), @catalogSrid) AS layer_srid
            FROM honua.layers
            WHERE layer_id = @layerId;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@layerId", layerId);
        command.Parameters.AddWithValue("@catalogSrid", CatalogExtentSrid);

        MaterializeRefreshMetadata? metadata;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            metadata = await ReadMaterializeRefreshMetadataAsync(reader, cancellationToken).ConfigureAwait(false);
        }

        if (metadata == null)
        {
            return null;
        }

        var fieldNames = await ReadLayerFieldNamesAsync(connection, metadata.LayerId, cancellationToken)
            .ConfigureAwait(false);
        return metadata with { FieldNames = fieldNames };
    }

    private static async Task<IReadOnlyList<MaterializeRefreshMetadata>> ResolveMaterializeRefreshMetadataBySourceAsync(
        string connectionString,
        string? schema,
        string table,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // When the caller knows the resolved schema, constrain by it; otherwise match every
        // published layer for the table name (the import path may not know which schema its
        // table resolved to). @schema is NULL in the latter case.
        const string sql = """
            SELECT
                layer_id,
                layer_name,
                table_schema,
                table_name,
                geometry_column,
                COALESCE(NULLIF(srid, 0), NULLIF(storage_srid, 0), @catalogSrid) AS layer_srid
            FROM honua.layers
            WHERE table_name = @table
              AND (@schema IS NULL OR table_schema = @schema)
            ORDER BY layer_id;
            """;

        var bases = new List<MaterializeRefreshMetadata>();
        await using (var command = new NpgsqlCommand(sql, connection))
        {
            command.Parameters.AddWithValue(
                "@schema",
                string.IsNullOrWhiteSpace(schema) ? DBNull.Value : schema);
            command.Parameters.AddWithValue("@table", table);
            command.Parameters.AddWithValue("@catalogSrid", CatalogExtentSrid);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (true)
            {
                var metadata = await ReadMaterializeRefreshMetadataAsync(reader, cancellationToken).ConfigureAwait(false);
                if (metadata == null)
                {
                    break;
                }

                bases.Add(metadata);
            }
        }

        if (bases.Count == 0)
        {
            return [];
        }

        var resolved = new List<MaterializeRefreshMetadata>(bases.Count);
        foreach (var metadata in bases)
        {
            var fieldNames = await ReadLayerFieldNamesAsync(connection, metadata.LayerId, cancellationToken)
                .ConfigureAwait(false);
            resolved.Add(metadata with { FieldNames = fieldNames });
        }

        return resolved;
    }

    private static async Task<MaterializeRefreshMetadata?> ReadMaterializeRefreshMetadataAsync(
        NpgsqlDataReader reader,
        CancellationToken cancellationToken)
    {
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (reader.IsDBNull(4))
        {
            // No geometry column recorded — not a materializable spatial layer.
            return null;
        }

        return new MaterializeRefreshMetadata(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5),
            []);
    }

    private static async Task<IReadOnlyList<string>> ReadLayerFieldNamesAsync(
        NpgsqlConnection connection,
        int layerId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT field_name
            FROM honua.layer_fields
            WHERE layer_id = @layerId
            ORDER BY field_order, field_name;
            """;

        var fieldNames = new List<string>();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@layerId", layerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            fieldNames.Add(reader.GetString(0));
        }

        return fieldNames;
    }

    private sealed record MaterializeRefreshMetadata(
        int LayerId,
        string LayerName,
        string Schema,
        string Table,
        string GeometryColumn,
        int Srid,
        IReadOnlyList<string> FieldNames);
}
