// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Db.Postgres.Features.Raster;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.Raster;

public sealed partial class PostgresRasterStoreGridTileIntegrationTests
{
    [IntegrationTest]
    public async Task GetImageTileAsync_CachedTileRequestedThroughAnotherLayer_IsNotServed()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreGridTileIntegrationTests));
        try
        {
            await CreateSchemaAsync(schemaName);
            var rasterId = await SeedConstant3857RasterAsync(schemaName, "cache-owner",
                -500000, 6500000, 50000, -55000, 100);
            await SeedCurrentCachedTileAsync(schemaName, rasterId);
            var store = CreateStore(schemaName);

            var throughOtherLayer = await store.GetImageTileAsync(LayerId + 1, rasterId, 15, 16383, 16384);
            var throughOwnLayer = await store.GetImageTileAsync(LayerId, rasterId, 15, 16383, 16384);

            throughOtherLayer.Should().BeNull("a raster id from another layer must not unlock that layer's cached tiles");
            throughOwnLayer.Should().NotBeNull();
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    private async Task SeedCurrentCachedTileAsync(string schemaName, long rasterId)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var seed = connection.CreateCommand();
        seed.CommandText = """
            CREATE TABLE raster_tiles (
                raster_data_id bigint, zoom_level integer, tile_x integer, tile_y integer,
                tile_data bytea, content_type text, renderer_version integer DEFAULT 0,
                renderer_payload_sha256 bytea);
            WITH body AS (
                SELECT ST_AsPNG(ST_AddBand(ST_MakeEmptyRaster(256, 256, 0, 0, 1, -1, 0, 0, 3857),
                    '8BUI'::text, 17, NULL)) AS bytes
            )
            INSERT INTO raster_tiles
            SELECT @id, 15, 16384, 16383, bytes, 'image/png', @version, sha256(bytes)
            FROM body;
            """;
        seed.Parameters.AddWithValue("id", rasterId);
        seed.Parameters.AddWithValue("version", RasterCorrectedNearestSql.RendererVersion);
        await seed.ExecuteNonQueryAsync();
    }
}
