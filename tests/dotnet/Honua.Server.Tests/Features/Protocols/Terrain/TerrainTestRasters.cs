// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.TestKit;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.Protocols.Terrain;

internal static class TerrainTestRasters
{
    internal const double WebMercatorExtent = 20037508.342789244;
    internal const int UnregisteredSrid = 999999;
    internal const string TerrainProtocolName = "Terrain";

    internal static readonly string[] AllProtocols =
    [
        "FeatureServer",
        "MapServer",
        "ImageServer",
        "GPServer",
        "OgcFeatures",
        "OGC-API-Maps",
        "OGC-API-Coverages",
        "OGC-API-Tiles",
        "Wfs20",
        "Wms",
        "Wmts",
        "Wcs",
        "OData",
        "Grpc",
        "Stac",
        TerrainProtocolName,
        "Elevation",
    ];

    internal static readonly string[] ProtocolsWithoutTerrain =
        AllProtocols.Where(static protocol => !string.Equals(protocol, TerrainProtocolName, StringComparison.Ordinal)).ToArray();

    internal static Task<IAsyncDisposable> HoldFullWorldAsync(WebAppFixture fixture, double elevationMeters)
        => HoldSeedAsync(fixture, FullWorldSeed with { Value = elevationMeters });

    internal static Task<IAsyncDisposable> HoldSouthEastQuadrantAsync(WebAppFixture fixture, double elevationMeters)
        => HoldSeedAsync(fixture, FullWorldSeed with
        {
            Name = "se-quadrant-dem",
            Width = 1,
            Height = 1,
            UpperLeftX = 0,
            UpperLeftY = 0,
            Value = elevationMeters
        });

    internal static Task<IAsyncDisposable> HoldUnknownCrsAsync(WebAppFixture fixture)
        => HoldRasterSqlAsync(fixture, """
            ST_AddBand(
                ST_MakeEmptyRaster(1, 1, @upperLeftX, @upperLeftY, @scaleX, @scaleY, 0, 0, 0),
                '32BF'::text,
                10,
                NULL)
            """);

    internal static Task<IAsyncDisposable> HoldUnregisteredCrsAsync(WebAppFixture fixture)
        => HoldRasterSqlAsync(fixture, $$"""
            ST_AddBand(
                ST_MakeEmptyRaster(1, 1, @upperLeftX, @upperLeftY, @scaleX, @scaleY, 0, 0, {{UnregisteredSrid}}),
                '32BF'::text,
                10,
                NULL)
            """);

    internal static Task<IAsyncDisposable> HoldAllSourceNoDataAsync(WebAppFixture fixture)
        => HoldRasterSqlAsync(fixture, """
            ST_AddBand(
                ST_MakeEmptyRaster(2, 2, @upperLeftX, @upperLeftY, @scaleX / 2, @scaleY / 2, 0, 0, 3857),
                '32BF'::text,
                -9999,
                -9999)
            """);

    internal static async Task<IAsyncDisposable> HoldRasterSqlAsync(WebAppFixture fixture, string rasterSql)
    {
        var schemaName = fixture.CurrentSchema
            ?? throw new InvalidOperationException("Fixture schema is not initialized.");

        // Hold the seed lock across the write and the case's reads: a parallel default
        // seed's honua.layers cleanup cascades to honua.raster_data.
        var lease = await fixture.Postgres.AcquireSchemaMutationLockAsync();
        try
        {
            await using var connection = await fixture.Postgres.GetConnectionAsync(schemaName);

            await using (var delete = connection.CreateCommand())
            {
                delete.CommandText = "DELETE FROM honua.raster_data WHERE layer_id = @layerId;";
                delete.Parameters.AddWithValue("layerId", WebAppFixture.TestLayerId);
                await delete.ExecuteNonQueryAsync();
            }

            await using var insert = connection.CreateCommand();
            insert.CommandText = $"""
                INSERT INTO honua.raster_data (layer_id, name, description, raster, acquisition_date, created_at)
                SELECT @layerId,
                       'unsupported-dem',
                       'Unsupported raster for terrain validation tests',
                       {rasterSql},
                       @acquisitionDate,
                       @createdAt;
                """;
            insert.Parameters.AddWithValue("layerId", WebAppFixture.TestLayerId);
            insert.Parameters.AddWithValue("upperLeftX", -WebMercatorExtent);
            insert.Parameters.AddWithValue("upperLeftY", WebMercatorExtent);
            insert.Parameters.AddWithValue("scaleX", 2 * WebMercatorExtent);
            insert.Parameters.AddWithValue("scaleY", -2 * WebMercatorExtent);
            insert.Parameters.AddWithValue("acquisitionDate", RasterIntegrationTestData.WestAcquisition.UtcDateTime);
            insert.Parameters.AddWithValue("createdAt", RasterIntegrationTestData.WestAcquisition.UtcDateTime);
            await insert.ExecuteNonQueryAsync();
        }
        catch
        {
            await lease.DisposeAsync();
            throw;
        }

        return lease;
    }

    private static readonly RasterSeed FullWorldSeed = new(
        Name: "world-dem",
        Width: 2,
        Height: 2,
        UpperLeftX: -WebMercatorExtent,
        UpperLeftY: WebMercatorExtent,
        ScaleX: WebMercatorExtent,
        ScaleY: -WebMercatorExtent,
        Value: 0,
        AcquisitionDate: RasterIntegrationTestData.WestAcquisition,
        CreatedAt: RasterIntegrationTestData.WestAcquisition,
        Srid: 3857);

    private static Task<IAsyncDisposable> HoldSeedAsync(WebAppFixture fixture, RasterSeed seed)
        => RasterIntegrationTestData.HoldLayerRastersAsync(fixture, WebAppFixture.TestLayerId, seed);
}
