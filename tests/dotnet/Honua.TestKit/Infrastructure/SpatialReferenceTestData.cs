// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.TestKit.Seeding;

namespace Honua.TestKit.Infrastructure;

/// <summary>
/// Helper methods for seeding and inspecting SRID-focused test data.
/// </summary>
public static class SpatialReferenceTestData
{
    public static Task SeedLayersAsync(PostgresFixture fixture, string schemaName)
    {
        return fixture.ApplySeedAsync(ResolveSeedPath(), schemaName);
    }

    public static async Task<long> InsertPointAsync(PostgresFixture fixture, string schemaName, int layerId, double lon, double lat, string name)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO features (layer_id, geometry, attributes)
            VALUES (
                @layerId,
                ST_Transform(ST_SetSRID(ST_MakePoint(@lon, @lat), 4326), @srid),
                jsonb_build_object('name', @name)
            )
            RETURNING objectid;
            """;
        command.Parameters.AddWithValue("layerId", layerId);
        command.Parameters.AddWithValue("lon", lon);
        command.Parameters.AddWithValue("lat", lat);
        command.Parameters.AddWithValue("srid", SpatialReferenceTestLayerCatalog.LayerSrid);
        command.Parameters.AddWithValue("name", name);

        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public static async Task<int?> GetGeometrySridAsync(PostgresFixture fixture, string schemaName, long objectId, int layerId)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ST_SRID(geometry)
            FROM features
            WHERE objectid = @objectId AND layer_id = @layerId;
            """;
        command.Parameters.AddWithValue("objectId", objectId);
        command.Parameters.AddWithValue("layerId", layerId);

        var result = await command.ExecuteScalarAsync();
        if (result == null || result == DBNull.Value)
        {
            return null;
        }

        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    public static async Task<(double X, double Y)?> GetGeometryCoordinatesAsync(
        PostgresFixture fixture,
        string schemaName,
        long objectId,
        int layerId,
        int? targetSrid = null)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = targetSrid.HasValue
            ? """
            SELECT ST_X(ST_Transform(geometry, @targetSrid)), ST_Y(ST_Transform(geometry, @targetSrid))
            FROM features
            WHERE objectid = @objectId AND layer_id = @layerId;
            """
            : """
            SELECT ST_X(geometry), ST_Y(geometry)
            FROM features
            WHERE objectid = @objectId AND layer_id = @layerId;
            """;
        command.Parameters.AddWithValue("objectId", objectId);
        command.Parameters.AddWithValue("layerId", layerId);
        if (targetSrid.HasValue)
        {
            command.Parameters.AddWithValue("targetSrid", targetSrid.Value);
        }

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        if (reader.IsDBNull(0) || reader.IsDBNull(1))
        {
            return null;
        }

        return (reader.GetDouble(0), reader.GetDouble(1));
    }

    public static async Task CleanupAsync(PostgresFixture fixture, string schemaName)
    {
        var sql = $"""
            DELETE FROM features WHERE layer_id IN ({SpatialReferenceTestLayerCatalog.PointLayerId}, {SpatialReferenceTestLayerCatalog.LineLayerId}, {SpatialReferenceTestLayerCatalog.PolygonLayerId});
            DELETE FROM honua.layer_fields WHERE layer_id IN ({SpatialReferenceTestLayerCatalog.PointLayerId}, {SpatialReferenceTestLayerCatalog.LineLayerId}, {SpatialReferenceTestLayerCatalog.PolygonLayerId});
            DELETE FROM honua.service_layers WHERE layer_id IN ({SpatialReferenceTestLayerCatalog.PointLayerId}, {SpatialReferenceTestLayerCatalog.LineLayerId}, {SpatialReferenceTestLayerCatalog.PolygonLayerId});
            DELETE FROM honua.layers WHERE layer_id IN ({SpatialReferenceTestLayerCatalog.PointLayerId}, {SpatialReferenceTestLayerCatalog.LineLayerId}, {SpatialReferenceTestLayerCatalog.PolygonLayerId});
            DELETE FROM honua.services WHERE service_name = '{SpatialReferenceTestLayerCatalog.ServiceId}';
            """;

        await fixture.ExecuteAsync(sql, schemaName);
    }

    private static string ResolveSeedPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Honua.sln")))
        {
            directory = directory.Parent;
        }

        if (directory == null)
        {
            throw new FileNotFoundException("Unable to locate repository root for seed data.");
        }

        return Path.Combine(directory.FullName, "tests", "seed", "spatial-reference.yaml");
    }
}
