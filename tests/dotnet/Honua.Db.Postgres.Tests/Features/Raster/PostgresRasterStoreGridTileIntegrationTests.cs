// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Globalization;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Tiles;
using Honua.Db.Postgres.Features.Raster;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Raster;

/// <summary>
/// Exercises the non-WebMercator gridset tile path (#2665) end-to-end against real PostGIS: the
/// bounds-based <see cref="PostgresRasterStore.GetImageTileAsync(int, long, RasterTileWindow, RasterFormat, System.Threading.CancellationToken)"/>
/// / <see cref="PostgresRasterStore.GetMosaicImageTileAsync(int, long[], RasterMergeStrategy, RasterTileWindow, RasterFormat, System.Threading.CancellationToken)"/>
/// overloads that render a WorldCRS84Quad (EPSG:4326) tile. The endpoint tests
/// (<c>ImageServerWmtsMatrixSetTests</c>) mock <c>IRasterStore</c>, so they never run this SQL;
/// this test seeds a real raster in EPSG:3857 — a DIFFERENT SRID than the 4326 gridset — so the
/// <c>ST_Transform</c>-into-gridset-SRID reprojection is genuinely executed, then asserts real,
/// correctly-placed pixels come back (not just "no exception"). The tile window is computed from
/// the one canonical <see cref="ITileMatrixSetRegistry"/> / <see cref="GridGeometry"/> so the test
/// drives the exact geometry the production tile handler uses.
/// </summary>
[Collection("Database")]
public sealed class PostgresRasterStoreGridTileIntegrationTests(PostgresFixture fixture)
{
    private const int LayerId = 9042;

    // WorldCRS84Quad level 2: 8 cols x 4 rows, tile span 45 degrees. Tile (col=4,row=1) covers
    // EPSG:4326 bounds [0,0,45,45] (northern/eastern quadrant), which lies well inside the valid
    // Web Mercator latitude band so the seeded 3857 source can fully cover it.
    private const int Level = 2;
    private const int TileCol = 4;
    private const int TileRow = 1;

    private const byte SourceValue = 100;
    private const byte WestValue = 100;
    private const byte EastValue = 200;

    private static string Inv(double value) => value.ToString("G17", CultureInfo.InvariantCulture);

    private static GridGeometry ResolveWorldCrs84Quad()
    {
        var registry = new TileMatrixSetRegistry(new TileMatrixSetDefinitionOptions());
        registry.TryGetGeometry(TileMatrixSetRegistry.WorldCrs84QuadId, Level, out var geometry)
            .Should().BeTrue("the WorldCRS84Quad gridset must be registered as a built-in");
        return geometry!;
    }

    private static RasterTileWindow BuildWindow(GridGeometry grid)
    {
        grid.Srid.Should().Be(4326, "WorldCRS84Quad renders in EPSG:4326, not the source 3857");
        var bounds = grid.GetTileBounds(TileCol, TileRow, Level);
        bounds.Should().NotBeNull();

        return new RasterTileWindow
        {
            MinX = bounds!.XMin,
            MinY = bounds.YMin,
            MaxX = bounds.XMax,
            MaxY = bounds.YMax,
            Srid = grid.Srid,
            TileWidth = grid.TileWidth,
            TileHeight = grid.TileHeight
        };
    }

    [IntegrationTest]
    public async Task GetImageTileAsync_TwoBandNativeFixture_StretchesEveryPixelWithinDeadline()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreGridTileIntegrationTests));
        try
        {
            await CreateSchemaAsync(schemaName);
            await using var connection = await fixture.GetConnectionAsync(schemaName);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE raster_tiles (
                    raster_data_id bigint, zoom_level integer, tile_x integer, tile_y integer,
                    tile_data bytea, content_type text, renderer_version integer DEFAULT 0,
                    renderer_payload_sha256 bytea);
                INSERT INTO raster_data (layer_id, name, raster)
                SELECT @layerId, 'native-two-band',
                    ST_SetValues(ST_SetValues(ST_AddBand(ST_AddBand(
                        ST_MakeEmptyRaster(4, 3, -124, 40, 0.25, -0.5, 0, 0, 4326),
                        '32BF'::text, -9999, -9999), '32BF'::text, -9999, -9999),
                        1, 1, 1, ARRAY[[-17.25, 0, 33.5, 8], [101, 2, -9999, -9], [10, 11, 12, 13]]::double precision[][]),
                        2, 1, 1, ARRAY[[200, 210, 220, -9999], [-4.5, 500, 600, 700], [800, 900, 1000, 1100]]::double precision[][])
                RETURNING id;
                """;
            command.Parameters.AddWithValue("layerId", LayerId);
            var rasterId = (long)(await command.ExecuteScalarAsync())!;

            // Use an independent pool so the bounded SQL setting cannot affect other tests.
            var settings = new NpgsqlConnectionStringBuilder(fixture.ConnectionString);
            settings.Options = $"{settings.Options} -c statement_timeout=15000".Trim();
            settings.CommandTimeout = 20;
            await using var dataSource = NpgsqlDataSource.Create(settings.ConnectionString);
            var store = new PostgresRasterStore(new FixtureConnectionProvider(dataSource),
                NullLogger<PostgresRasterStore>.Instance, FixtureBypassDatabaseSchemaGuard.Instance, schemaName);

            // This invokes the complete production cache miss -> statistics -> sampler ->
            // per-band auto-stretch -> aligned frame -> encoder, not the sampler alone.
            var totalRendering = TimeSpan.Zero;
            byte[]? geotiff = null;
            command.Parameters.Clear();
            foreach (var format in new[] { RasterFormat.TIFF, RasterFormat.PNG })
            {
                command.CommandText = "SELECT COUNT(*) FROM raster_tiles";
                ((long)(await command.ExecuteScalarAsync())!).Should().Be(0,
                    "both encoders must exercise a real production cache miss");
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var tile = await store.GetImageTileAsync(LayerId, rasterId, 9, 195, 80, format, deadline.Token)
                    ?? throw new InvalidOperationException("Expected the actual native fixture tile.");
                watch.Stop();
                watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));
                totalRendering += watch.Elapsed;
                tile.Width.Should().Be(256);
                tile.Height.Should().Be(256);
                tile.Srid.Should().Be(3857);
                if (format == RasterFormat.TIFF)
                {
                    geotiff = tile.Data;
                }
                else
                {
                    tile.Data.Take(8).Should().Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
                    System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(tile.Data.AsSpan(16, 4)).Should().Be(256);
                    System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(tile.Data.AsSpan(20, 4)).Should().Be(256);
                }
            }
            totalRendering.Should().BeLessThan(TimeSpan.FromSeconds(30));

            command.CommandText = """
                WITH decoded AS (SELECT ST_FromGDALRaster(@data) AS rast)
                SELECT ST_Width(rast), ST_Height(rast), ST_NumBands(rast), ST_SRID(rast),
                    ST_BandPixelType(rast, 1), ST_BandPixelType(rast, 2),
                    ST_BandNoDataValue(rast, 1), ST_BandNoDataValue(rast, 2),
                    ST_DumpValues(rast, 1, FALSE), ST_DumpValues(rast, 2, FALSE)
                FROM decoded;
                """;
            command.Parameters.Clear();
            command.Parameters.AddWithValue("data", geotiff!);
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetInt32(0).Should().Be(256);
            reader.GetInt32(1).Should().Be(256);
            reader.GetInt32(2).Should().Be(2);
            reader.GetInt32(3).Should().Be(3857);
            reader.GetString(4).Should().Be("8BUI");
            reader.GetString(5).Should().Be("8BUI");
            reader.GetDouble(6).Should().Be(0);
            reader.GetDouble(7).Should().Be(0);
            // Independent literal results for the fixed source and each band's full-source
            // min/max. PostGIS 8BUI conversion truncates positive clamped values; both source
            // NoData (-9999) and the resulting 8BUI NoData are represented here by zero.
            double[][,] literalStretchedBands =
            [
                new double[,] { { 0, 37, 109, 54 }, { 255, 41, 0, 17 }, { 58, 60, 63, 65 } },
                new double[,] { { 47, 49, 51, 0 }, { 0, 116, 139, 162 }, { 185, 208, 231, 255 } }
            ];
            const double radius = 6378137;
            const double halfWorld = 20037508.342789244;
            var tileSpan = 2 * halfWorld / 512;
            for (var band = 0; band < 2; band++)
            {
                var actual = reader.GetFieldValue<double[,]>(8 + band);
                actual.GetLength(0).Should().Be(256);
                actual.GetLength(1).Should().Be(256);
                for (var y = 0; y < 256; y++)
                {
                    var mercatorY = halfWorld - (195 + (y + 0.5) / 256) * tileSpan;
                    var latitude = (2 * Math.Atan(Math.Exp(mercatorY / radius)) - Math.PI / 2) * 180 / Math.PI;
                    var sourceY = (int)Math.Floor((40 - latitude) / 0.5);
                    for (var x = 0; x < 256; x++)
                    {
                        var longitude = (-halfWorld + (80 + (x + 0.5) / 256) * tileSpan) / radius * 180 / Math.PI;
                        var sourceX = (int)Math.Floor((longitude + 124) / 0.25);
                        var expected = sourceX is >= 0 and < 4 && sourceY is >= 0 and < 3
                            ? literalStretchedBands[band][sourceY, sourceX] : 0;
                        actual[y, x].Should().Be(expected, $"literal stretched band {band + 1}, target ({x},{y})");
                    }
                }
            }
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task GetImageTileAsync_WorldCrs84QuadWindow_ReprojectsSource3857AndRendersRealPixels()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreGridTileIntegrationTests));
        try
        {
            await CreateSchemaAsync(schemaName);

            // A constant 8BUI raster in EPSG:3857 that fully covers the 4326 tile footprint
            // [0,0,45,45] once reprojected. In 3857 the tile spans x[0,5.01e6], y[0,5.62e6]; this
            // source spans x[-500000,5900000], y[-540000,6500000] (lon ~[-4.5,53], lat ~[-4.9,50.4])
            // so the whole tile — and its centre point (lon 22.5, lat 22.5) — is covered.
            var rasterId = await SeedConstant3857RasterAsync(
                schemaName, name: "crs84-source",
                upperLeftX: -500000, upperLeftY: 6500000,
                scaleX: 50000, scaleY: -55000, value: SourceValue);

            var grid = ResolveWorldCrs84Quad();
            var window = BuildWindow(grid);
            window.Srid.Should().Be(4326);

            var store = CreateStore(schemaName);

            // Primary assertion path uses GeoTIFF, whose georeferencing survives the round trip, so
            // the decoded tile can be checked in world coordinates: it must be in EPSG:4326 (the
            // gridset SRID — proving ST_Transform reprojected the 3857 source), on the WorldCRS84Quad
            // cell grid, and carry the source value at the tile-centre ground point.
            var geotiff = await store.GetImageTileAsync(LayerId, rasterId, window, RasterFormat.TIFF);
            geotiff.Should().NotBeNull("the gridset tile SQL must return a rendered tile");
            var geotiffTile = geotiff is { } g ? g : throw new InvalidOperationException("Expected a rendered GeoTIFF tile.");
            geotiffTile.Width.Should().Be(256);
            geotiffTile.Height.Should().Be(256);
            geotiffTile.Srid.Should().Be(4326);
            geotiffTile.Data.Should().NotBeEmpty();

            var expectedCellSize = (window.MaxX - window.MinX) / 256.0; // 45deg / 256px
            var decoded = await InspectGeoTiffTileAsync(schemaName, geotiffTile.Data);
            decoded.Width.Should().Be(256);
            decoded.Height.Should().Be(256);
            decoded.Srid.Should().Be(4326, "the source in 3857 must be reprojected into the 4326 gridset SRID");
            decoded.ScaleX.Should().BeApproximately(expectedCellSize, 1e-6,
                "the rendered tile must sit on the WorldCRS84Quad cell grid (45deg / 256px)");
            decoded.CentreValue.Should().Be(SourceValue,
                "the reprojected 3857 source value must survive into the 4326 tile at the tile centre");

            // And the PNG encoding branch still returns real PNG bytes for the same window.
            var png = await store.GetImageTileAsync(LayerId, rasterId, window, RasterFormat.PNG);
            png.Should().NotBeNull();
            var pngTile = png is { } p ? p : throw new InvalidOperationException("Expected a rendered PNG tile.");
            pngTile.Data.Take(4).Should().Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, "the tile must be a real PNG");
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task GetMosaicImageTileAsync_WorldCrs84QuadWindow_CompositesReprojectedSources()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreGridTileIntegrationTests));
        try
        {
            await CreateSchemaAsync(schemaName);

            // Two constant 3857 rasters that share the SAME pixel grid alignment (identical scale,
            // origins offset by an integer number of pixels) so the mosaic-statistics ST_Union can
            // combine them. Each is 64x64 px at 50000 m/px in X, 110000 m/px in Y. "west" (value 100)
            // is anchored at x=-1200000; "east" (value 200) sits exactly 64 pixels east (x=2000000).
            // West covers x[-1200000,2000000] (lon ~[-10.8,18.0]) and east x[2000000,5200000]
            // (lon ~[18.0,46.7]); both span y[-540000,6500000] (lat ~[-4.9,50.4]). Ground points
            // (lon 5, lat 22.5) and (lon 40, lat 22.5) fall cleanly inside the west and east
            // footprints, so a correct mosaic must composite BOTH independently reprojected sources.
            const double scaleX = 50000;
            const double scaleY = -110000;
            var west = await SeedConstant3857RasterAsync(
                schemaName, name: "west",
                upperLeftX: -1200000, upperLeftY: 6500000,
                scaleX: scaleX, scaleY: scaleY, value: WestValue, dimension: 64);
            var east = await SeedConstant3857RasterAsync(
                schemaName, name: "east",
                upperLeftX: -1200000 + (64 * scaleX), upperLeftY: 6500000,
                scaleX: scaleX, scaleY: scaleY, value: EastValue, dimension: 64);

            var grid = ResolveWorldCrs84Quad();
            var window = BuildWindow(grid);

            var store = CreateStore(schemaName);
            var result = await store.GetMosaicImageTileAsync(
                LayerId, [west, east], RasterMergeStrategy.Newest, window, RasterFormat.TIFF);

            var tile = result
                ?? throw new InvalidOperationException("The gridset mosaic tile SQL must return a rendered tile.");
            tile.Width.Should().Be(256);
            tile.Height.Should().Be(256);
            tile.Srid.Should().Be(4326);
            tile.Data.Should().NotBeEmpty();

            var decoded = await InspectGeoTiffTileAsync(schemaName, tile.Data);
            decoded.Width.Should().Be(256);
            decoded.Height.Should().Be(256);
            decoded.Srid.Should().Be(4326, "the mosaic must be reprojected into the 4326 gridset SRID");

            // West ground point must carry the west source, east ground point the east source: the
            // mosaic composited two independently reprojected 3857 rasters onto the 4326 tile grid.
            var westSample = await SampleGeoTiffAtWorldPointAsync(schemaName, result.Value.Data, lon: 5, lat: 22.5);
            var eastSample = await SampleGeoTiffAtWorldPointAsync(schemaName, result.Value.Data, lon: 40, lat: 22.5);
            westSample.Should().Be(WestValue, "the west ground point must carry the reprojected west source");
            eastSample.Should().Be(EastValue, "the east ground point must carry the reprojected east source");
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetImageTileAsync_ByteBand_PreservesNoDataMetadataWhenPadding(bool hasNoData)
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreGridTileIntegrationTests));
        try
        {
            await CreateSchemaAsync(schemaName);
            await using var connection = await fixture.GetConnectionAsync(schemaName);
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO raster_data (layer_id, name, raster)
                VALUES (@layerId, 'valid-zero', ST_SetValue(
                    ST_AddBand(ST_MakeEmptyRaster(2, 1, 1, 1, 1, -1, 0, 0, 4326),
                        '8BUI'::text, 0, @noData), 1, 2, 1, 200))
                RETURNING id;
                """;
            seed.Parameters.AddWithValue("layerId", LayerId);
            seed.Parameters.AddWithValue("noData", NpgsqlTypes.NpgsqlDbType.Double,
                hasNoData ? (object)255.0 : DBNull.Value);
            var rasterId = (long)(await seed.ExecuteScalarAsync())!;
            var window = new RasterTileWindow
            {
                MinX = 0,
                MinY = 0,
                MaxX = 4,
                MaxY = 1,
                Srid = 4326,
                TileWidth = 4,
                TileHeight = 1
            };

            var tile = await CreateStore(schemaName).GetImageTileAsync(
                LayerId, rasterId, window, RasterFormat.TIFF)
                ?? throw new InvalidOperationException("Expected a padded GeoTIFF tile.");
            await using var inspect = connection.CreateCommand();
            inspect.CommandText = """
                WITH decoded AS (SELECT ST_FromGDALRaster(@data) AS rast)
                SELECT ST_Width(rast), ST_Height(rast),
                       ST_Value(rast, 1, 2, 1), ST_Value(rast, 1, 3, 1),
                       ST_Value(rast, 1, 1, 1), ST_Value(rast, 1, 4, 1),
                       ST_BandNoDataValue(rast, 1)
                FROM decoded;
                """;
            inspect.Parameters.AddWithValue("data", tile.Data);
            await using var reader = await inspect.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetInt32(0).Should().Be(4, "both uncovered edge cells must be present");
            reader.GetInt32(1).Should().Be(1);
            reader.IsDBNull(2).Should().BeFalse("zero is valid source data, not a NoData sentinel");
            reader.GetDouble(2).Should().Be(0);
            reader.GetDouble(3).Should().Be(200);
            reader.IsDBNull(4).Should().Be(hasNoData);
            reader.IsDBNull(5).Should().Be(hasNoData);
            reader.IsDBNull(6).Should().Be(!hasNoData);
            if (hasNoData)
            {
                reader.GetDouble(6).Should().Be(255, "an existing source sentinel must be retained");
            }
            else
            {
                reader.GetDouble(4).Should().Be(0, "a band without NoData uses opaque zero background");
                reader.GetDouble(5).Should().Be(0, "a band without NoData uses opaque zero background");
            }
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task FrameAlignedRaster_MixedBandMetadata_PreservesTypesAndEachNoDataValue()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreGridTileIntegrationTests));
        try
        {
            await using var connection = await fixture.GetConnectionAsync(schemaName);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                WITH source AS (
                    SELECT ST_AddBand(ST_AddBand(
                        ST_MakeEmptyRaster(2, 1, 1, 1, 1, -1, 0, 0, 4326),
                        '8BUI'::text, 0, NULL), '16BSI'::text, 0, -9999) AS rast
                ), grid AS (
                    SELECT ST_MakeEmptyRaster(4, 1, 0, 1, 1, -1, 0, 0, 4326) AS rast
                ), framed AS MATERIALIZED (
                    SELECT {RasterGridFrameSql.FrameAlignedRaster("s.rast", "g.rast")} AS rast
                    FROM source s, grid g
                )
                SELECT ST_NumBands(rast), ST_BandPixelType(rast, 1), ST_BandPixelType(rast, 2),
                       ST_BandNoDataValue(rast, 1), ST_BandNoDataValue(rast, 2),
                       ST_Value(rast, 1, 2, 1), ST_Value(rast, 2, 2, 1),
                       ST_Value(rast, 1, 1, 1), ST_Value(rast, 2, 1, 1)
                FROM framed;
                """;
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetInt32(0).Should().Be(2);
            reader.GetString(1).Should().Be("8BUI");
            reader.GetString(2).Should().Be("16BSI");
            reader.IsDBNull(3).Should().BeTrue();
            reader.GetDouble(4).Should().Be(-9999);
            reader.GetDouble(5).Should().Be(0, "the byte band's zero is valid data");
            reader.GetDouble(6).Should().Be(0, "the signed band's zero differs from its sentinel");
            reader.GetDouble(7).Should().Be(0, "the band without NoData retains background padding");
            reader.IsDBNull(8).Should().BeTrue("the signed band's padding uses its existing sentinel");
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTheory]
    [InlineData("current", 17.0)]
    [InlineData("legacy", 100.0)]
    [InlineData("missing-hash", 100.0)]
    [InlineData("stale-hash", 100.0)]
    [InlineData("old-overwrite", 100.0)]
    [InlineData("unproven-overview", 100.0)]
    public async Task GetImageTileAsync_AdmitsOnlyCurrentUnchangedCachePayload(string state, double expected)
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreGridTileIntegrationTests));
        try
        {
            await CreateSchemaAsync(schemaName);
            var rasterId = await SeedConstant3857RasterAsync(schemaName, "cache-source",
                -500000, 6500000, 50000, -55000, 100);
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
                SELECT @id, 15, 16384, 16383, bytes, 'image/png', @version,
                       CASE WHEN @state = 'missing-hash' THEN NULL
                            WHEN @state = 'stale-hash' THEN sha256(decode('00', 'hex'))
                            ELSE sha256(bytes) END
                FROM body;
                """;
            seed.Parameters.AddWithValue("id", rasterId);
            seed.Parameters.AddWithValue("version", state is "legacy" or "unproven-overview"
                ? 0 : RasterCorrectedNearestSql.RendererVersion);
            seed.Parameters.AddWithValue("state", state);
            await seed.ExecuteNonQueryAsync();
            if (state == "old-overwrite")
            {
                seed.CommandText = """
                    UPDATE raster_tiles SET tile_data = ST_AsPNG(ST_AddBand(
                        ST_MakeEmptyRaster(256, 256, 0, 0, 1, -1, 0, 0, 3857), '8BUI'::text, 99, NULL));
                    """;
                await seed.ExecuteNonQueryAsync();
            }

            if (state == "unproven-overview")
            {
                // A plausible overview whose pixels are wrong must not bypass the native source.
                seed.CommandText = """
                    CREATE TABLE raster_overviews (id bigint, raster_data_id bigint, ground_resolution double precision, raster raster);
                    INSERT INTO raster_overviews VALUES (1, @id, 1, ST_AddBand(
                        ST_MakeEmptyRaster(4, 4, 0, 1300, 400, -400, 0, 0, 3857), '8BUI'::text, 99, NULL));
                    """;
                await seed.ExecuteNonQueryAsync();
            }

            seed.CommandText = "SELECT encode(sha256(tile_data), 'hex') FROM raster_tiles";
            var before = (string)(await seed.ExecuteScalarAsync())!;
            var tile = await CreateStore(schemaName).GetImageTileAsync(LayerId, rasterId, 15, 16383, 16384)
                ?? throw new InvalidOperationException("Expected a source or admitted cached tile.");
            seed.CommandText = "SELECT ST_Value(ST_FromGDALRaster(@data), 1, 128, 128, FALSE)";
            seed.Parameters.AddWithValue("data", tile.Data);
            ((double)(await seed.ExecuteScalarAsync())!).Should().Be(expected);
            seed.CommandText = "SELECT encode(sha256(tile_data), 'hex') FROM raster_tiles";
            ((string)(await seed.ExecuteScalarAsync())!).Should().Be(before,
                "cache rejection preserves the historical payload instead of deleting or regenerating it");
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetMosaicImageTileAsync_CoverageMaskPreservesOtherSourceAndRestoresBandMetadata(bool declaredNoData)
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreGridTileIntegrationTests));
        try
        {
            await CreateSchemaAsync(schemaName);
            await using var connection = await fixture.GetConnectionAsync(schemaName);
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO raster_data (layer_id, name, raster, created_at)
                VALUES (@layer, 'west', ST_SetValue(ST_AddBand(
                    ST_MakeEmptyRaster(2, 1, 0, 1, 1, -1, 0, 0, 4326),
                    '8BUI'::text, 0, @nodata), 1, 2, 1, 100), '2024-01-01'),
                    (@layer, 'east', ST_AddBand(ST_MakeEmptyRaster(1, 1, 2, 1, 1, -1, 0, 0, 4326),
                    '8BUI'::text, 200, @nodata), '2024-01-02')
                RETURNING id;
                """;
            seed.Parameters.AddWithValue("layer", LayerId);
            seed.Parameters.AddWithValue("nodata", NpgsqlTypes.NpgsqlDbType.Double,
                declaredNoData ? 255.0 : DBNull.Value);
            var ids = new List<long>();
            await using (var rows = await seed.ExecuteReaderAsync())
            {
                while (await rows.ReadAsync())
                {
                    ids.Add(rows.GetInt64(0));
                }
            }

            var tile = await CreateStore(schemaName).GetMosaicImageTileAsync(LayerId, ids.ToArray(),
                RasterMergeStrategy.Newest, new RasterTileWindow
                {
                    MinX = -1,
                    MaxX = 3,
                    MinY = 0,
                    MaxY = 1,
                    Srid = 4326,
                    TileWidth = 4,
                    TileHeight = 1
                }, RasterFormat.TIFF) ?? throw new InvalidOperationException("Expected a composed mosaic tile.");
            seed.CommandText = """
                WITH decoded AS (SELECT ST_FromGDALRaster(@data) AS rast)
                SELECT ST_BandPixelType(rast, 1), ST_BandNoDataValue(rast, 1),
                       ST_Value(rast, 1, 1, 1), ST_Value(rast, 1, 2, 1),
                       ST_Value(rast, 1, 3, 1), ST_Value(rast, 1, 4, 1) FROM decoded;
                """;
            seed.Parameters.AddWithValue("data", tile.Data);
            await using var reader = await seed.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetString(0).Should().Be("8BUI");
            reader.IsDBNull(1).Should().Be(!declaredNoData);
            reader.IsDBNull(2).Should().Be(declaredNoData);
            if (declaredNoData)
            {
                reader.GetDouble(1).Should().Be(255);
            }
            else
            {
                reader.GetDouble(2).Should().Be(0);
            }

            reader.GetDouble(3).Should().Be(0, "valid zero remains visible");
            reader.GetDouble(4).Should().Be(100, "the newer source's outside mask cannot erase the west source");
            reader.GetDouble(5).Should().Be(200, "the west source's outside mask cannot erase the east source");
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTheory]
    [InlineData(RasterMergeStrategy.Newest, false)]
    [InlineData(RasterMergeStrategy.Oldest, false)]
    [InlineData(RasterMergeStrategy.Average, true)]
    [InlineData(RasterMergeStrategy.Max, true)]
    [InlineData(RasterMergeStrategy.Min, true)]
    public async Task GetMosaicImageTileAsync_MixedBandMetadata_MatchesCanonicalUnion(
        RasterMergeStrategy strategy, bool floatFirst)
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreGridTileIntegrationTests));
        try
        {
            await CreateSchemaAsync(schemaName);
            await using var connection = await fixture.GetConnectionAsync(schemaName);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                WITH inputs AS (
                    SELECT ST_AddBand(ST_AddBand(ST_MakeEmptyRaster(1, 1, 0, 1, 1, -1, 0, 0, 4326),
                        '32BF'::text, 2.75, -9999), '32BF'::text, 6.5, -9998) AS floating,
                        ST_AddBand(ST_AddBand(ST_MakeEmptyRaster(1, 1, 0, 1, 1, -1, 0, 0, 4326),
                        '8BUI'::text, 20, 255), '16BUI'::text, 40, 65535) AS integer
                )
                INSERT INTO raster_data (id, layer_id, name, raster, acquisition_date, created_at)
                SELECT 1, @layer, 'first-id', CASE WHEN @floatFirst THEN floating ELSE integer END,
                    CASE WHEN @floatFirst THEN '2024-01-01'::timestamptz ELSE '2024-01-02'::timestamptz END,
                    '2024-01-01'::timestamptz FROM inputs
                UNION ALL
                SELECT 2, @layer, 'second-id', CASE WHEN @floatFirst THEN integer ELSE floating END,
                    CASE WHEN @floatFirst THEN '2024-01-02'::timestamptz ELSE '2024-01-01'::timestamptz END,
                    '2024-01-02'::timestamptz FROM inputs;
                """;
            command.Parameters.AddWithValue("layer", LayerId);
            command.Parameters.AddWithValue("floatFirst", floatFirst);
            await command.ExecuteNonQueryAsync();

            var tile = await CreateStore(schemaName).GetMosaicImageTileAsync(LayerId, [1L, 2L], strategy,
                new RasterTileWindow
                {
                    MinX = 0,
                    MaxX = 1,
                    MinY = 0,
                    MaxY = 1,
                    Srid = 4326,
                    TileWidth = 1,
                    TileHeight = 1
                }, RasterFormat.TIFF) ?? throw new InvalidOperationException("Expected a typed mosaic.");
            command.CommandText = $"""
                WITH source AS MATERIALIZED (
                    SELECT raster AS rast, id, created_at,
                        COALESCE(acquisition_date, created_at) AS effective_acquisition
                    FROM raster_data ORDER BY id
                ), canonical_raw AS (
                    SELECT {RasterMosaicSql.CreateMosaicAggregateExpression(strategy)} AS rast FROM source
                ), canonical AS (
                    -- GTiff has one shared NoData tag. Compare the same encoded contract;
                    -- distinct raw per-band markers are checked before encoding separately.
                    SELECT ST_FromGDALRaster(ST_AsGDALRaster(rast, 'GTiff')) AS rast FROM canonical_raw
                ), actual AS (SELECT ST_FromGDALRaster(@data) AS rast)
                SELECT ST_BandPixelType(a.rast, n), ST_BandPixelType(c.rast, n),
                       ST_BandNoDataValue(a.rast, n), ST_BandNoDataValue(c.rast, n),
                       ST_Value(a.rast, n, 1, 1), ST_Value(c.rast, n, 1, 1)
                FROM actual a, canonical c, generate_series(1, 2) n ORDER BY n;
                """;
            command.Parameters.AddWithValue("data", tile.Data);
            await using var reader = await command.ExecuteReaderAsync();
            for (var band = 0; band < 2; band++)
            {
                (await reader.ReadAsync()).Should().BeTrue();
                reader.GetString(0).Should().Be(reader.GetString(1));
                reader.GetDouble(2).Should().Be(reader.GetDouble(3));
                reader.GetDouble(4).Should().Be(reader.GetDouble(5));
                if (strategy == RasterMergeStrategy.Average)
                {
                    reader.GetDouble(4).Should().Be(band == 0 ? 11.375 : 23.25,
                        "the canonical float aggregate must retain fractional values");
                }
            }

            (await reader.ReadAsync()).Should().BeFalse();
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTheory]
    [InlineData(false, false, 1)]
    [InlineData(false, true, 1)]
    [InlineData(false, false, 2)]
    [InlineData(false, true, 2)]
    public Task GetImageTileAsync_NumericPngOpacity_FollowsDisplayedBandValidity(
        bool mosaic, bool explicitGrid, int bands)
        => VerifyNumericPngOpacityAsync(mosaic, explicitGrid, bands, legacyCache: false);

    // Separate entrypoint names allow a focused mosaic run without selecting single-raster rows.
    [IntegrationTheory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public Task GetMosaicImageTileAsync_NumericPngOpacity_FollowsDisplayedBandValidity(bool explicitGrid, int bands)
        => VerifyNumericPngOpacityAsync(mosaic: true, explicitGrid, bands, legacyCache: false);

    [IntegrationTheory]
    [InlineData(true)]
    public Task GetMosaicImageTileAsync_NumericPngOpacity_TwoSourcesRetainDisplayedBandCoverage(bool explicitGrid)
        => VerifyNumericPngOpacityAsync(mosaic: true, explicitGrid, bands: 2, legacyCache: false, fillNoData: true);

    [IntegrationTest]
    public Task GetMosaicImageTileAsync_NumericPngOpacity_TwoSourcesWebMercatorWithinDeadline()
        => VerifyNumericPngOpacityAsync(mosaic: true, explicitGrid: false, bands: 2, legacyCache: false, fillNoData: true);

    [IntegrationTest]
    public Task GetImageTileAsync_NumericPngOpacity_RejectsOldAlphaCacheWithoutDeletingIt()
        => VerifyNumericPngOpacityAsync(mosaic: false, explicitGrid: false, bands: 2, legacyCache: true);

    [IntegrationTest]
    public async Task GetImageTileAsync_GrayAlphaPng_PreservesIndependentAlpha()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreGridTileIntegrationTests));
        try
        {
            await CreateSchemaAsync(schemaName);
            await using var connection = await fixture.GetConnectionAsync(schemaName);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO raster_data (layer_id, name, raster, second_band_is_alpha)
                SELECT @layerId, 'gray-alpha',
                    ST_SetValues(
                        ST_AddBand(
                            ST_SetValues(
                                ST_AddBand(
                                    ST_MakeEmptyRaster(2, 2, 1000, 3000, 1000, -1000, 0, 0, 3857),
                                    '8BUI'::text, 1, NULL),
                                1, 1, 1, ARRAY[[10, 200], [40, 0]]::double precision[][]),
                            '8BUI'::text, 1, NULL),
                        2, 1, 1, ARRAY[[0, 128], [255, 255]]::double precision[][]),
                    TRUE
                RETURNING id;
                """;
            command.Parameters.AddWithValue("layerId", LayerId);
            var rasterId = (long)(await command.ExecuteScalarAsync())!;
            var window = new RasterTileWindow
            {
                MinX = 1000,
                MinY = 1000,
                MaxX = 3000,
                MaxY = 3000,
                Srid = 3857,
                TileWidth = 2,
                TileHeight = 2
            };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var tile = await CreateStore(schemaName).GetImageTileAsync(
                LayerId, rasterId, window, RasterFormat.PNG, deadline.Token)
                ?? throw new InvalidOperationException("Gray-alpha PNG tile is missing.");

            command.Parameters.Clear();
            command.CommandText = """
                WITH image AS (SELECT ST_FromGDALRaster(@png) AS rast)
                SELECT ST_NumBands(rast), ST_Value(rast, 1, col, row, FALSE), ST_Value(rast, 2, col, row, FALSE)
                FROM image, (VALUES (1, 1, 10.0, 0.0), (2, 1, 200.0, 128.0), (1, 2, 40.0, 255.0), (2, 2, 0.0, 255.0))
                    AS sample(col, row, gray, alpha)
                ORDER BY row, col;
                """;
            command.Parameters.AddWithValue("png", tile.Data);
            await using var reader = await command.ExecuteReaderAsync();
            foreach (var (gray, alpha) in new[] { (10d, 0d), (200d, 128d), (40d, 255d), (0d, 255d) })
            {
                (await reader.ReadAsync()).Should().BeTrue();
                reader.GetInt32(0).Should().Be(2);
                reader.GetDouble(1).Should().Be(gray);
                reader.GetDouble(2).Should().Be(alpha, "band two is the source alpha, not a NoData mask");
            }

            (await reader.ReadAsync()).Should().BeFalse();
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task GetImageTileAsync_UnsignedTwoBandWithoutAlphaMetadata_SynthesizesOpacity()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreGridTileIntegrationTests));
        try
        {
            await CreateSchemaAsync(schemaName);
            await using var connection = await fixture.GetConnectionAsync(schemaName);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO raster_data (layer_id, name, raster)
                SELECT @layerId, 'scientific-two-band',
                    ST_SetValues(
                        ST_AddBand(
                            ST_SetValues(
                                ST_AddBand(
                                    ST_MakeEmptyRaster(2, 2, 1000, 3000, 1000, -1000, 0, 0, 3857),
                                    '8BUI'::text, 1, NULL),
                                1, 1, 1, ARRAY[[10, 200], [40, 0]]::double precision[][]),
                            '8BUI'::text, 1, NULL),
                        2, 1, 1, ARRAY[[0, 128], [255, 255]]::double precision[][])
                RETURNING id;
                """;
            command.Parameters.AddWithValue("layerId", LayerId);
            var rasterId = (long)(await command.ExecuteScalarAsync())!;
            var window = new RasterTileWindow
            {
                MinX = 1000,
                MinY = 1000,
                MaxX = 3000,
                MaxY = 3000,
                Srid = 3857,
                TileWidth = 2,
                TileHeight = 2
            };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var tile = await CreateStore(schemaName).GetImageTileAsync(
                LayerId, rasterId, window, RasterFormat.PNG, deadline.Token)
                ?? throw new InvalidOperationException("Scientific two-band PNG tile is missing.");

            command.Parameters.Clear();
            command.CommandText = """
                WITH image AS (SELECT ST_FromGDALRaster(@png) AS rast)
                SELECT ST_Value(rast, 2, col, row, FALSE)
                FROM image, (VALUES (1, 1), (2, 1), (1, 2), (2, 2)) AS sample(col, row)
                ORDER BY row, col;
                """;
            command.Parameters.AddWithValue("png", tile.Data);
            await using var reader = await command.ExecuteReaderAsync();
            for (var index = 0; index < 4; index++)
            {
                (await reader.ReadAsync()).Should().BeTrue();
                reader.GetDouble(0).Should().Be(255, "unsigned band two is scientific data unless import recorded alpha");
            }

            (await reader.ReadAsync()).Should().BeFalse();
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    private async Task VerifyNumericPngOpacityAsync(bool mosaic, bool explicitGrid, int bands, bool legacyCache, bool fillNoData = false)
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreGridTileIntegrationTests));
        try
        {
            await CreateSchemaAsync(schemaName);
            await using var connection = await fixture.GetConnectionAsync(schemaName);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE raster_tiles (
                    raster_data_id bigint, zoom_level integer, tile_x integer, tile_y integer,
                    tile_data bytea, content_type text, renderer_version integer DEFAULT 0,
                    renderer_payload_sha256 bytea);
                WITH first_band AS (
                    SELECT ST_SetValues(ST_AddBand(
                        ST_MakeEmptyRaster(2, 2, 1000, 3000, 1000, -1000, 0, 0, 3857),
                        '32BF'::text, -9999, -9999), 1, 1, 1,
                        ARRAY[[-17.25, 101], [-9999, 0]]::double precision[][]) AS rast
                )
                INSERT INTO raster_data (layer_id, name, raster)
                SELECT @layerId, 'numeric-opacity',
                    CASE WHEN @bands = 1 THEN rast ELSE
                        ST_SetValues(ST_AddBand(rast, '32BF'::text, -9999, -9999), 2, 1, 1,
                            ARRAY[[-9999, -4.5], [600, 1100]]::double precision[][])
                    END
                FROM first_band RETURNING id;
                """;
            command.Parameters.AddWithValue("layerId", LayerId);
            command.Parameters.AddWithValue("bands", bands);
            var rasterId = (long)(await command.ExecuteScalarAsync())!;
            command.Parameters.Clear();

            var rasterIds = new[] { rasterId };
            if (fillNoData)
            {
                command.CommandText = """
                    INSERT INTO raster_data (layer_id, name, raster, created_at)
                    SELECT @layerId, 'numeric-opacity-overlay',
                        ST_AddBand(ST_AddBand(ST_MakeEmptyRaster(1, 1, 1000, 2000, 1000, -1000, 0, 0, 3857),
                            '32BF'::text, 101, -9999), '32BF'::text, -9999, -9999), NOW() + INTERVAL '1 second'
                    RETURNING id;
                    """;
                command.Parameters.AddWithValue("layerId", LayerId);
                rasterIds = [rasterId, (long)(await command.ExecuteScalarAsync())!];
                command.Parameters.Clear();
            }

            if (legacyCache)
            {
                command.CommandText = """
                    WITH encoded AS (
                        SELECT ST_AsGDALRaster(ST_AddBand(ST_AddBand(
                            ST_MakeEmptyRaster(256, 256, 0, 256, 1, -1, 0, 0, 3857),
                            '8BUI'::text, 50, NULL), '8BUI'::text, 0, NULL), 'PNG') AS data
                    )
                    INSERT INTO raster_tiles
                        (raster_data_id, zoom_level, tile_x, tile_y, tile_data, content_type,
                         renderer_version, renderer_payload_sha256)
                    SELECT @id, 10, 512, 511, data, 'image/png', 1, sha256(data) FROM encoded;
                    """;
                command.Parameters.AddWithValue("id", rasterId);
                await command.ExecuteNonQueryAsync();
                command.Parameters.Clear();
            }

            var registry = new TileMatrixSetRegistry(new TileMatrixSetDefinitionOptions());
            registry.TryGetGeometry(TileMatrixSetRegistry.WebMercatorQuadId, 10, out var geometry).Should().BeTrue();
            var bounds = geometry!.GetTileBounds(512, 511, 10)!;
            var window = new RasterTileWindow
            {
                MinX = explicitGrid ? 0 : bounds.XMin,
                MinY = explicitGrid ? 0 : bounds.YMin,
                MaxX = explicitGrid ? 4000 : bounds.XMax,
                MaxY = explicitGrid ? 4000 : bounds.YMax,
                Srid = 3857,
                TileWidth = 256,
                TileHeight = 256
            };
            var store = CreateStore(schemaName);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var tile = (mosaic, explicitGrid) switch
            {
                (false, false) => await store.GetImageTileAsync(LayerId, rasterId, 10, 511, 512,
                    RasterFormat.PNG, cancellationToken: deadline.Token),
                (false, true) => await store.GetImageTileAsync(LayerId, rasterId, window,
                    RasterFormat.PNG, cancellationToken: deadline.Token),
                (true, false) => await store.GetMosaicImageTileAsync(LayerId, rasterIds, RasterMergeStrategy.Newest,
                    10, 511, 512, RasterFormat.PNG, cancellationToken: deadline.Token),
                (true, true) => await store.GetMosaicImageTileAsync(LayerId, rasterIds, RasterMergeStrategy.Newest,
                    window, RasterFormat.PNG, cancellationToken: deadline.Token)
            } ?? throw new InvalidOperationException("Numeric PNG tile is missing.");

            // Decode the actual PNG, including its physical alpha or tRNS mask. Inspecting
            // TIFF samples or only the PNG signature cannot prove display opacity.
            command.CommandText = """
                WITH image AS (SELECT ST_FromGDALRaster(@png) AS rast),
                sample AS (SELECT rast, ST_Value(rast, 1, @x, @y, FALSE) AS gray FROM image)
                SELECT gray,
                    CASE WHEN ST_NumBands(rast) IN (2, 4)
                        THEN ST_Value(rast, ST_NumBands(rast), @x, @y, FALSE)
                        WHEN gray IS NOT DISTINCT FROM ST_BandNoDataValue(rast, 1) THEN 0
                        ELSE 255 END AS opacity
                FROM sample;
                """;
            foreach (var (x, y, expectedGray, expectedOpacity) in new[]
                     { (1500d, 2500d, 0d, 255d), (2500d, 2500d, 255d, 255d),
                       (1500d, 1500d, fillNoData ? 255d : 0d, fillNoData ? 255d : 0d), (2500d, 1500d, 37d, 255d) })
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("png", tile.Data);
                command.Parameters.AddWithValue("x", 1 + (int)Math.Floor((x - window.MinX) * 256 / (window.MaxX - window.MinX)));
                command.Parameters.AddWithValue("y", 1 + (int)Math.Floor((window.MaxY - y) * 256 / (window.MaxY - window.MinY)));
                await using var reader = await command.ExecuteReaderAsync();
                (await reader.ReadAsync()).Should().BeTrue();
                reader.GetDouble(1).Should().Be(expectedOpacity, "display opacity follows band one, not scientific band two");
                reader.GetDouble(0).Should().Be(expectedGray);
            }

            if (legacyCache)
            {
                command.Parameters.Clear();
                command.CommandText = "SELECT COUNT(*) FROM raster_tiles WHERE renderer_version = 1 AND renderer_payload_sha256 = sha256(tile_data)";
                ((long)(await command.ExecuteScalarAsync())!).Should().Be(1, "old cached evidence is retained, not rewritten");
            }
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTheory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task GetImageTileAsync_NumericPngOpacity_PreservesRgbAndRgba(int bands)
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreGridTileIntegrationTests));
        try
        {
            await CreateSchemaAsync(schemaName);
            await using var connection = await fixture.GetConnectionAsync(schemaName);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO raster_data (layer_id, name, raster)
                SELECT @layer, 'presentation', ST_AddBand(
                    ST_MakeEmptyRaster(1, 1, 0, 1, 1, -1, 0, 0, 3857), ARRAY(
                        SELECT ROW(NULL, '8BUI', CASE WHEN n = 4 THEN 64 ELSE n * 10 END, NULL)::addbandarg
                        FROM generate_series(1, @bands) n ORDER BY n)) RETURNING id;
                """;
            command.Parameters.AddWithValue("layer", LayerId);
            command.Parameters.AddWithValue("bands", bands);
            var id = (long)(await command.ExecuteScalarAsync())!;
            var tile = await CreateStore(schemaName).GetImageTileAsync(LayerId, id,
                new RasterTileWindow
                {
                    MinX = 0,
                    MinY = 0,
                    MaxX = 1,
                    MaxY = 1,
                    Srid = 3857,
                    TileWidth = 1,
                    TileHeight = 1
                }, RasterFormat.PNG)
                ?? throw new InvalidOperationException("Missing presentation tile.");
            command.Parameters.Clear();
            command.CommandText = """
                WITH image AS (SELECT ST_FromGDALRaster(@png) AS rast)
                SELECT ST_NumBands(rast), ST_Value(rast, n, 1, 1, FALSE)
                FROM image, generate_series(1, ST_NumBands(rast)) n ORDER BY n;
                """;
            command.Parameters.AddWithValue("png", tile.Data);
            await using var reader = await command.ExecuteReaderAsync();
            for (var band = 1; band <= bands; band++)
            {
                (await reader.ReadAsync()).Should().BeTrue();
                reader.GetInt32(0).Should().Be(bands);
                reader.GetDouble(1).Should().Be(band == 4 ? 64 : band * 10);
            }
            (await reader.ReadAsync()).Should().BeFalse();
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RasterTileExecution_LocalJitSetting_DoesNotLeak(bool fail)
    {
        await using var connection = await fixture.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SHOW jit";
        var original = (string)(await command.ExecuteScalarAsync())!;
        command.CommandText = fail ? "SELECT 1 / 0" : "SELECT current_setting('jit')";
        if (fail)
        {
            var error = await Assert.ThrowsAsync<PostgresException>(async () =>
                await RasterTileExecution.ExecuteScalarAsync(command, CancellationToken.None));
            error.SqlState.Should().Be("22012");
        }
        else
        {
            (await RasterTileExecution.ExecuteScalarAsync(command, CancellationToken.None)).Should().Be("off");
        }
        command.Transaction.Should().BeNull();
        command.CommandText = "SHOW jit";
        ((string)(await command.ExecuteScalarAsync())!).Should().Be(original);
    }

    private PostgresRasterStore CreateStore(string schemaName)
        => new(
            new FixtureConnectionProvider(fixture.DataSource),
            NullLogger<PostgresRasterStore>.Instance,
            FixtureBypassDatabaseSchemaGuard.Instance,
            schemaName);

    private async Task CreateSchemaAsync(string schemaName)
    {
        // raster_data: the source-of-truth table the tile SQL scans. raster_statistics: required by
        // the per-raster auto-stretch statistics path (its backfill does not auto-create the table).
        // raster_layer_statistics is intentionally absent: this fixture bypasses the production
        // journal guard and exercises the read-only compute fallback without runtime DDL.
        await fixture.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS raster_data (
                id BIGSERIAL PRIMARY KEY,
                layer_id INTEGER NOT NULL,
                name VARCHAR(255) NOT NULL,
                raster raster NOT NULL,
                acquisition_date TIMESTAMPTZ,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at TIMESTAMPTZ,
                srid INTEGER GENERATED ALWAYS AS (ST_SRID(raster)) STORED,
                second_band_is_alpha BOOLEAN NOT NULL DEFAULT FALSE
            );
            CREATE TABLE IF NOT EXISTS raster_statistics (
                id BIGSERIAL PRIMARY KEY,
                raster_data_id BIGINT NOT NULL,
                band_number INTEGER NOT NULL,
                min_value DOUBLE PRECISION,
                max_value DOUBLE PRECISION,
                mean_value DOUBLE PRECISION,
                std_dev DOUBLE PRECISION,
                valid_pixel_count BIGINT,
                nodata_pixel_count BIGINT,
                computed_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                CONSTRAINT raster_statistics_unique_band UNIQUE (raster_data_id, band_number)
            );
            """, schemaName);
    }

    private async Task<long> SeedConstant3857RasterAsync(
        string schemaName,
        string name,
        double upperLeftX,
        double upperLeftY,
        double scaleX,
        double scaleY,
        byte value,
        int dimension = 128)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO raster_data (layer_id, name, raster, acquisition_date, created_at)
            SELECT @layerId,
                   @name,
                   ST_AddBand(
                       ST_MakeEmptyRaster(
                           {dimension.ToString(CultureInfo.InvariantCulture)},
                           {dimension.ToString(CultureInfo.InvariantCulture)},
                           {Inv(upperLeftX)}, {Inv(upperLeftY)},
                           {Inv(scaleX)}, {Inv(scaleY)}, 0, 0, 3857),
                       '8BUI'::text,
                       @value,
                       NULL),
                   NOW(),
                   NOW()
            RETURNING id;
            """;
        command.Parameters.AddWithValue("layerId", LayerId);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("value", (double)value);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    // Decodes the returned GeoTIFF tile in PostGIS (GeoTIFF preserves georeferencing, so the CRS
    // and cell size survive the round trip) and reports its SRID, X cell size, and the pixel value
    // at the tile-centre ground point (lon 22.5, lat 22.5 for tile col=4,row=1,level=2).
    private async Task<(int Srid, double ScaleX, double CentreValue, int Width, int Height)> InspectGeoTiffTileAsync(
        string schemaName, byte[] tile)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH decoded AS (SELECT ST_FromGDALRaster(@data) AS rast)
            SELECT ST_SRID(rast),
                   ST_ScaleX(rast),
                   ST_Value(rast, 1, ST_SetSRID(ST_MakePoint(22.5, 22.5), 4326)),
                   ST_Width(rast), ST_Height(rast)
            FROM decoded;
            """;
        command.Parameters.AddWithValue("data", tile);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue("the decoded tile must yield a raster row");
        var srid = reader.GetInt32(0);
        var scaleX = reader.GetDouble(1);
        reader.IsDBNull(2).Should().BeFalse("the tile centre must be a covered (non-NODATA) pixel");
        var centre = reader.GetDouble(2);
        return (srid, scaleX, centre, reader.GetInt32(3), reader.GetInt32(4));
    }

    private async Task<double> SampleGeoTiffAtWorldPointAsync(string schemaName, byte[] tile, double lon, double lat)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ST_Value(ST_FromGDALRaster(@data), 1, ST_SetSRID(ST_MakePoint(@lon, @lat), 4326));
            """;
        command.Parameters.AddWithValue("data", tile);
        command.Parameters.AddWithValue("lon", lon);
        command.Parameters.AddWithValue("lat", lat);
        var value = await command.ExecuteScalarAsync();
        value.Should().NotBeNull("the sampled ground point must be covered, not NODATA");
        return Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }

    private sealed class FixtureConnectionProvider(NpgsqlDataSource dataSource) : IAdoNetDatabaseConnectionProvider
    {
        public string GetConnectionString() => dataSource.ConnectionString;

        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => await dataSource.OpenConnectionAsync(cancellationToken);

        public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
        {
            var connection = await OpenConnectionAsync(cancellationToken);
            try
            {
                var transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken);
                return (connection, transaction);
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }

        public async Task<T> ExecuteWithDeadlockRetryAsync<T>(
            Func<Task<T>> operation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await operation();
        }

        public async Task ExecuteWithDeadlockRetryAsync(
            Func<Task> operation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await operation();
        }
    }
}
