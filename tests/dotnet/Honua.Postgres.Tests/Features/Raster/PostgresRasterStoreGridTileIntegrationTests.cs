// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Globalization;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Tiles;
using Honua.Postgres.Features.Raster;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Honua.Postgres.Tests.Features.Raster;

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

            result.Should().NotBeNull("the gridset mosaic tile SQL must return a rendered tile");
            result!.Value.Width.Should().Be(256);
            result.Value.Height.Should().Be(256);
            result.Value.Srid.Should().Be(4326);
            result.Value.Data.Should().NotBeEmpty();

            var decoded = await InspectGeoTiffTileAsync(schemaName, result.Value.Data);
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

    private PostgresRasterStore CreateStore(string schemaName)
        => new(
            new FixtureConnectionProvider(fixture.DataSource),
            NullLogger<PostgresRasterStore>.Instance,
            schemaName);

    private async Task CreateSchemaAsync(string schemaName)
    {
        // raster_data: the source-of-truth table the tile SQL scans. raster_statistics: required by
        // the per-raster auto-stretch statistics path (its backfill does not auto-create the table).
        // The mosaic path's raster_layer_statistics table is created on demand by the store.
        await fixture.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS raster_data (
                id BIGSERIAL PRIMARY KEY,
                layer_id INTEGER NOT NULL,
                name VARCHAR(255) NOT NULL,
                raster raster NOT NULL,
                acquisition_date TIMESTAMPTZ,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at TIMESTAMPTZ,
                srid INTEGER GENERATED ALWAYS AS (ST_SRID(raster)) STORED
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
    private async Task<(int Srid, double ScaleX, double CentreValue)> InspectGeoTiffTileAsync(
        string schemaName, byte[] tile)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH decoded AS (SELECT ST_FromGDALRaster(@data) AS rast)
            SELECT ST_SRID(rast),
                   ST_ScaleX(rast),
                   ST_Value(rast, 1, ST_SetSRID(ST_MakePoint(22.5, 22.5), 4326))
            FROM decoded;
            """;
        command.Parameters.AddWithValue("data", tile);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue("the decoded tile must yield a raster row");
        var srid = reader.GetInt32(0);
        var scaleX = reader.GetDouble(1);
        reader.IsDBNull(2).Should().BeFalse("the tile centre must be a covered (non-NODATA) pixel");
        var centre = reader.GetDouble(2);
        return (srid, scaleX, centre);
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
