// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.
//
// Partial: Layer + service extent recomputation.
//
// SQL helpers that read source-table geometry to derive a layer's catalog-SRID envelope,
// update honua.layers.extent for a single layer, recompute the union envelope on
// honua.services.service_extent, and load the schema/table/SRID metadata needed to refresh
// an extent. The mirror that propagates refreshed envelopes into the Metadata v2 graph
// (SyncRefreshedExtentsIntoV2GraphAsync) lives in the MetadataV2Graph partial because it
// touches IMetadataV2GraphStore. Extracted into its own file so extent-recomputation SQL
// stays in one place independent of the broader publish/persist flow.

using Honua.Core.Features.Admin.Domain;
using Npgsql;

namespace Honua.Postgres.Features.Admin;

internal sealed partial class PostgreSqlLayerPublishingService
{
    private static async Task<LayerExtentInsert?> ReadLayerExtentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        string table,
        string geometryColumn,
        int sourceSrid,
        CancellationToken cancellationToken)
    {
        var qualifiedTable = $"{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}";
        var quotedGeometryColumn = QuoteIdentifier(geometryColumn);
        var normalizedSourceSrid = sourceSrid > 0 ? sourceSrid : CatalogExtentSrid;
        var sql = $"""
            WITH source_geometries AS (
                SELECT {quotedGeometryColumn}::geometry AS geom
                FROM {qualifiedTable}
                WHERE {quotedGeometryColumn} IS NOT NULL
            ),
            catalog_geometries AS (
                SELECT
                    CASE
                        WHEN geom IS NULL OR ST_IsEmpty(geom) THEN NULL
                        WHEN COALESCE(NULLIF(ST_SRID(geom), 0), @sourceSrid) = @catalogSrid
                            THEN ST_SetSRID(geom, @catalogSrid)
                        ELSE ST_Transform(
                            ST_SetSRID(geom, COALESCE(NULLIF(ST_SRID(geom), 0), @sourceSrid)),
                            @catalogSrid)
                    END AS geom
                FROM source_geometries
            )
            SELECT ST_XMin(extent), ST_YMin(extent), ST_XMax(extent), ST_YMax(extent)
            FROM (
                SELECT ST_Extent(geom) AS extent
                FROM catalog_geometries
                WHERE geom IS NOT NULL
            ) AS extent_query;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@sourceSrid", normalizedSourceSrid);
        command.Parameters.AddWithValue("@catalogSrid", CatalogExtentSrid);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0))
        {
            return null;
        }

        return new LayerExtentInsert(
            reader.GetDouble(0),
            reader.GetDouble(1),
            reader.GetDouble(2),
            reader.GetDouble(3),
            CatalogExtentSrid);
    }

    private static async Task<(LayerExtentRefreshLayerResult Public, LayerExtentInsert? Extent)?> RefreshLayerExtentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int layerId,
        CancellationToken cancellationToken)
    {
        var metadata = await GetLayerExtentRefreshMetadataAsync(
                connection,
                transaction,
                layerId,
                cancellationToken)
            .ConfigureAwait(false);
        if (metadata == null)
        {
            return null;
        }

        var extent = await ReadLayerExtentAsync(
                connection,
                transaction,
                metadata.Schema,
                metadata.Table,
                metadata.GeometryColumn,
                metadata.SourceSrid,
                cancellationToken)
            .ConfigureAwait(false);

        const string sql = """
            UPDATE honua.layers
            SET extent = CASE
                WHEN @extentMinX IS NULL THEN NULL
                ELSE ST_MakeEnvelope(@extentMinX, @extentMinY, @extentMaxX, @extentMaxY, @extentSrid)
            END
            WHERE layer_id = @layerId;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@layerId", layerId);
        AddNullableDouble(command, "@extentMinX", extent?.MinX);
        AddNullableDouble(command, "@extentMinY", extent?.MinY);
        AddNullableDouble(command, "@extentMaxX", extent?.MaxX);
        AddNullableDouble(command, "@extentMaxY", extent?.MaxY);
        command.Parameters.AddWithValue("@extentSrid", extent?.Srid ?? CatalogExtentSrid);
        await command.ExecuteNonQueryAsync(cancellationToken);

        var publicResult = new LayerExtentRefreshLayerResult
        {
            LayerId = metadata.LayerId,
            LayerName = metadata.LayerName,
            HasExtent = extent != null,
            ExtentSrid = extent?.Srid
        };
        return (publicResult, extent);
    }

    private static async Task UpdateServiceExtentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string serviceName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH extent_box AS (
                SELECT ST_Extent(l.extent) AS box
                FROM honua.service_layers sl
                INNER JOIN honua.layers l
                    ON l.layer_id = sl.layer_id
                WHERE sl.service_name = @serviceName
                  AND l.enabled = TRUE
                  AND l.extent IS NOT NULL
            ),
            computed_extent AS (
                SELECT
                    CASE
                        WHEN box IS NULL THEN NULL
                        ELSE ST_MakeEnvelope(
                            ST_XMin(box),
                            ST_YMin(box),
                            ST_XMax(box),
                            ST_YMax(box),
                            @catalogSrid)
                    END AS extent
                FROM extent_box
            )
            UPDATE honua.services AS service
            SET service_extent = computed_extent.extent,
                updated_at = NOW()
            FROM computed_extent
            WHERE service.service_name = @serviceName;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@serviceName", serviceName);
        command.Parameters.AddWithValue("@catalogSrid", CatalogExtentSrid);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<LayerExtentRefreshMetadata?> GetLayerExtentRefreshMetadataAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int layerId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                layer_id,
                layer_name,
                table_schema,
                table_name,
                geometry_column,
                COALESCE(NULLIF(storage_srid, 0), NULLIF(srid, 0), @catalogSrid) AS source_srid
            FROM honua.layers
            WHERE layer_id = @layerId;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@layerId", layerId);
        command.Parameters.AddWithValue("@catalogSrid", CatalogExtentSrid);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (reader.IsDBNull(4))
        {
            return null;
        }

        return new LayerExtentRefreshMetadata(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5));
    }
}
