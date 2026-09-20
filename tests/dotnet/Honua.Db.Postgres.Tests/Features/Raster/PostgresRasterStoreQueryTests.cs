// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Globalization;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Db.Postgres.Features.Raster;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Raster;

[Collection("Database")]
public sealed class PostgresRasterStoreQueryTests(PostgresFixture fixture)
{
    private const int LayerId = 9002;

    [IntegrationTest]
    public async Task QueryRastersAsync_WithTimestampAndGeometry_UsesLayerSnapshotBeforeGeometryFilter()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreQueryTests));
        try
        {
            await CreateRasterTableAsync(schemaName);
            await InsertRasterAsync(
                schemaName,
                "older-local",
                DateTimeOffset.Parse("2024-02-01T00:00:00Z", CultureInfo.InvariantCulture),
                upperLeftX: 0,
                upperLeftY: 1);
            await InsertRasterAsync(
                schemaName,
                "newer-remote",
                DateTimeOffset.Parse("2024-03-01T00:00:00Z", CultureInfo.InvariantCulture),
                upperLeftX: 10,
                upperLeftY: 1);

            var store = new PostgresRasterStore(
                new FixtureConnectionProvider(fixture.DataSource),
                NullLogger<PostgresRasterStore>.Instance,
                FixtureBypassDatabaseSchemaGuard.Instance,
                schemaName);
            var localSelection = new RasterSelectionQuery
            {
                Geometry = CreateEnvelopeWkb(0, 0, 1, 1),
                GeometrySrid = 4326
            };

            var currentSelection = await store.QueryRastersAsync(LayerId, localSelection).ConfigureAwait(false);
            currentSelection.Should().ContainSingle().Which.Name.Should().Be("older-local");

            var temporalSelection = await store.QueryRastersAsync(
                LayerId,
                localSelection with
                {
                    Timestamp = DateTimeOffset.Parse("2024-04-01T00:00:00Z", CultureInfo.InvariantCulture)
                }).ConfigureAwait(false);

            temporalSelection.Should().BeEmpty(
                "the newest layer snapshot before the timestamp has no raster in the requested geometry");
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task ExportImageAsync_WithBandSelection_ExportsRequestedBandCount()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreQueryTests));
        try
        {
            await CreateRasterTableAsync(schemaName);
            var rasterId = await InsertMultiBandRasterAsync(schemaName);
            var store = new PostgresRasterStore(
                new FixtureConnectionProvider(fixture.DataSource),
                NullLogger<PostgresRasterStore>.Instance,
                FixtureBypassDatabaseSchemaGuard.Instance,
                schemaName);

            var result = await store.ExportImageAsync(
                    LayerId,
                    rasterId,
                    new RasterQuery
                    {
                        OutputFormat = RasterFormat.TIFF,
                        Bands = [2]
                    })
                .ConfigureAwait(false);

            result.Data.Should().NotBeEmpty();
            result.BandCount.Should().Be(1);
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task ExportImageAsync_WithImageServerQuery_ReturnsPngInsteadOfThrowing()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreQueryTests));
        try
        {
            await CreateRasterTableAsync(schemaName);
            var rasterId = await InsertImageServerProbeRasterAsync(schemaName);
            var store = new PostgresRasterStore(
                new FixtureConnectionProvider(fixture.DataSource),
                NullLogger<PostgresRasterStore>.Instance,
                FixtureBypassDatabaseSchemaGuard.Instance,
                schemaName);

            var result = await store.ExportImageAsync(
                    LayerId,
                    rasterId,
                    new RasterQuery
                    {
                        OutputFormat = RasterFormat.PNG,
                        ClipRegion = new RasterClipRegion
                        {
                            Geometry = CreateEnvelopeWkb(-123, 37, -121, 39),
                            Srid = 4326,
                        },
                        OutputSrid = 4326,
                        OutputWidth = 64,
                        OutputHeight = 64,
                        ResamplingAlgorithm = ResamplingAlgorithm.Bilinear,
                    })
                .ConfigureAwait(false);

            result.Data.Should().StartWith([0x89, 0x50, 0x4E, 0x47]);
            result.ContentType.Should().Be("image/png");
            result.Width.Should().Be(64);
            result.Height.Should().Be(64);
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTheory]
    [InlineData("single")]
    [InlineData("mosaic")]
    [InlineData("map")]
    public async Task Export_WithUnchangedCrs_PreservesNonSquarePixelGrid(string path)
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreQueryTests));
        try
        {
            await CreateRasterTableAsync(schemaName);
            var rasterId = await InsertQuadrantRasterAsync(schemaName);
            await using (var connection = await fixture.GetConnectionAsync(schemaName))
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE raster_data SET raster = ST_SetUpperLeft(ST_SetScale(raster, 2, -1), 0, 2) WHERE id = @id";
                command.Parameters.AddWithValue("id", rasterId);
                await command.ExecuteNonQueryAsync();
            }

            var store = CreateStore(schemaName);
            var query = new RasterQuery { OutputFormat = RasterFormat.TIFF, OutputSrid = 4326 };
            var result = path switch
            {
                "single" => await store.ExportImageAsync(LayerId, rasterId, query),
                "mosaic" => await store.ExportMosaicAsync(LayerId, [rasterId], RasterMergeStrategy.Newest, query),
                _ => await new PostgresRasterMapRenderer(
                    new FixtureConnectionProvider(fixture.DataSource),
                    NullLogger<PostgresRasterMapRenderer>.Instance, schemaName)
                    .RenderCollectionMapAsync(LayerId, new MapRenderRequest
                    {
                        BoundingBox = [0, 0, 4, 2],
                        BoundingBoxCrs = 4326,
                        Crs = 4326,
                        Width = 2,
                        Height = 2,
                        Format = RasterFormat.TIFF
                    })
            };

            // Decode actual exported bytes, not just the returned metadata. A same-CRS
            // ST_Transform used to resample this 2x2 grid into square pixels.
            var decoded = await ProbeExportedRasterAsync(schemaName, result.Data, 4326,
                (1, 1.5), (3, 1.5), (1, 0.5), (3, 0.5));
            decoded.Width.Should().Be(2);
            decoded.Height.Should().Be(2);
            decoded.XMin.Should().BeApproximately(0, 1e-9);
            decoded.XMax.Should().BeApproximately(4, 1e-9);
            decoded.YMin.Should().BeApproximately(0, 1e-9);
            decoded.YMax.Should().BeApproximately(2, 1e-9);
            decoded.Values.Should().Equal(1, 2, 3, 4);
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTheory]
    [InlineData("single", 64, 64)]
    [InlineData("mosaic", 64, 64)]
    [InlineData("map", 64, 64)]
    [InlineData("single", 32, 16)]
    [InlineData("mosaic", 32, 16)]
    [InlineData("map", 32, 16)]
    public async Task Export_WithExplicitDimensions_PreservesGeographicGrid(string path, int width, int height)
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreQueryTests));
        try
        {
            await CreateRasterTableAsync(schemaName);
            var rasterId = await InsertImageServerProbeRasterAsync(schemaName);
            await using (var connection = await fixture.GetConnectionAsync(schemaName))
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE raster_data SET raster = ST_SetUpperLeft(ST_SetScale(raster, 0.00234375, -0.0021875), -122.5, 37.84) WHERE id = @id";
                command.Parameters.AddWithValue("id", rasterId);
                await command.ExecuteNonQueryAsync();
            }

            var store = CreateStore(schemaName);
            var query = new RasterQuery
            {
                OutputFormat = RasterFormat.TIFF,
                OutputSrid = 4326,
                OutputWidth = width,
                OutputHeight = height,
                ClipRegion = new RasterClipRegion
                {
                    Geometry = CreateEnvelopeWkb(-122.5, 37.7, -122.35, 37.84),
                    Srid = 4326
                }
            };
            var result = path switch
            {
                "single" => await store.ExportImageAsync(LayerId, rasterId, query),
                "mosaic" => await store.ExportMosaicAsync(LayerId, [rasterId], RasterMergeStrategy.Newest, query),
                _ => await new PostgresRasterMapRenderer(
                    new FixtureConnectionProvider(fixture.DataSource),
                    NullLogger<PostgresRasterMapRenderer>.Instance, schemaName)
                    .RenderCollectionMapAsync(LayerId, new MapRenderRequest
                    {
                        BoundingBox = [-122.5, 37.7, -122.35, 37.84],
                        BoundingBoxCrs = 4326,
                        Crs = 4326,
                        Width = width,
                        Height = height,
                        Format = RasterFormat.TIFF
                    })
            };

            // Decode the TIFF: metadata dimensions alone missed ST_Resize changing
            // the extent even for a 64x64 input and a requested 64x64 output.
            var decoded = await ProbeExportedRasterAsync(schemaName, result.Data, 4326,
                (-122.5 + 0.075 / width, 37.84 - 0.07 / height),
                (-122.35 - 0.075 / width, 37.7 + 0.07 / height));
            decoded.Width.Should().Be(width);
            decoded.Height.Should().Be(height);
            decoded.XMin.Should().BeApproximately(-122.5, 1e-9);
            decoded.XMax.Should().BeApproximately(-122.35, 1e-9);
            decoded.YMin.Should().BeApproximately(37.7, 1e-9);
            decoded.YMax.Should().BeApproximately(37.84, 1e-9);
            decoded.Values.Should().Equal(7, 7);
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTheory]
    [InlineData(64, 64)]
    [InlineData(59, 57)]
    [InlineData(32, 16)]
    public async Task Resize_RotatedGrid_PreservesFootprintAndExpectedSamples(int width, int height)
    {
        await using var connection = await fixture.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH pattern AS (
                SELECT ST_SetValues(ST_AddBand(
                    ST_MakeEmptyRaster(64, 64, -122.5, 37.84, 0.00234375, -0.0021875, 0.0005, 0.0003, 4326),
                    '32BF'::text, 0, -9999), 1, 1, 1,
                    ARRAY(SELECT ARRAY(SELECT (y * 1000 + x)::double precision
                        FROM generate_series(1, 64) x) FROM generate_series(1, 64) y)) AS original
            ), resized AS (
                SELECT original, {RasterProjectionSql.ResizePreservingGrid("original", "@width", "@height")} AS rast
                FROM pattern
            )
            SELECT ST_Width(rast), ST_Height(rast), ST_SRID(rast),
                ST_HausdorffDistance(ST_ConvexHull(original), ST_ConvexHull(rast)),
                (SELECT count(*) FROM generate_series(1, @width) x CROSS JOIN generate_series(1, @height) y
                 WHERE ST_Value(rast, 1, x, y) IS DISTINCT FROM
                    ((floor((y - 0.5) * 64 / @height) + 1) * 1000
                        + floor((x - 0.5) * 64 / @width) + 1)::double precision)
            FROM resized
            """;
        command.Parameters.AddWithValue("width", width);
        command.Parameters.AddWithValue("height", height);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt32(0).Should().Be(width);
        reader.GetInt32(1).Should().Be(height);
        reader.GetInt32(2).Should().Be(4326);
        reader.GetDouble(3).Should().BeApproximately(0, 1e-9);
        reader.GetInt64(4).Should().Be(0, "every output pixel must match center-based nearest-neighbor sampling");
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExportImageAsync_WithDisabledGdalDrivers_ReportsUnsupportedFormat(bool mosaic)
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreQueryTests));
        try
        {
            await CreateRasterTableAsync(schemaName);
            var rasterId = await InsertImageServerProbeRasterAsync(schemaName);
            var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            {
                Options = "-c postgis.gdal_enabled_drivers=DISABLE_ALL",
            };
            await using var dataSource = NpgsqlDataSource.Create(connectionString.ConnectionString);
            var store = new PostgresRasterStore(
                new FixtureConnectionProvider(dataSource),
                NullLogger<PostgresRasterStore>.Instance,
                FixtureBypassDatabaseSchemaGuard.Instance,
                schemaName);

            var query = new RasterQuery
            {
                OutputFormat = RasterFormat.PNG,
                OutputWidth = 64,
                OutputHeight = 64,
            };
            var secondRasterId = mosaic ? await InsertImageServerProbeRasterAsync(schemaName) : rasterId;
            var export = () => mosaic
                ? store.ExportMosaicAsync(LayerId, [rasterId, secondRasterId], RasterMergeStrategy.Newest, query)
                : store.ExportImageAsync(LayerId, rasterId, query);

            await export.Should().ThrowAsync<NotSupportedException>()
                .WithMessage("The requested raster export format is not available on this server.");
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task ExportImageAsync_WithMinMaxStretch_RescalesFloatRasterTo8Bit()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreQueryTests));
        try
        {
            await CreateRasterTableAsync(schemaName);
            await CreateRasterStatisticsTableAsync(schemaName);
            var rasterId = await InsertFloatRasterAsync(schemaName);
            var store = new PostgresRasterStore(
                new FixtureConnectionProvider(fixture.DataSource),
                NullLogger<PostgresRasterStore>.Instance,
                FixtureBypassDatabaseSchemaGuard.Instance,
                schemaName);

            var result = await store.ExportImageAsync(
                    LayerId,
                    rasterId,
                    new RasterQuery
                    {
                        OutputFormat = RasterFormat.TIFF,
                        Stretch = new RasterStretch { StretchType = RasterStretchType.MinMax },
                    })
                .ConfigureAwait(false);

            result.Data.Should().NotBeEmpty();

            // Re-import the exported GeoTIFF and confirm the stretch produced an 8-bit
            // band spanning the full 0..255 display range (0,10,20,30 -> 0,85,170,255).
            var (pixelType, min, max) = await SummarizeExportedRasterAsync(schemaName, result.Data);
            pixelType.Should().Be("8BUI");
            min.Should().Be(0);
            max.Should().Be(255);
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task GetClippedStatisticsAsync_RestrictsAnalysisToAoiEnvelope()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreQueryTests));
        try
        {
            await CreateRasterTableAsync(schemaName);
            var rasterId = await InsertFloatRasterAsync(schemaName);
            var store = new PostgresRasterStore(
                new FixtureConnectionProvider(fixture.DataSource),
                NullLogger<PostgresRasterStore>.Instance,
                FixtureBypassDatabaseSchemaGuard.Instance,
                schemaName);

            // Raster pixels: x[0,1] holds 0 (top) and 20 (bottom); x[1,2] holds 10/30.
            // Clip to the left column only -> the analysis must see just {0, 20}.
            var leftColumn = CreateEnvelopeWkb(0, 0, 1, 2);

            var stats = await store.GetClippedStatisticsAsync(LayerId, rasterId, leftColumn, 4326)
                .ConfigureAwait(false);

            stats.Should().ContainSingle();
            stats[0].MinValue.Should().Be(0);
            stats[0].MaxValue.Should().Be(20);
            stats[0].ValidPixelCount.Should().Be(2);

            var histograms = await store.GetClippedHistogramsAsync(LayerId, rasterId, leftColumn, 4326, binCount: 4)
                .ConfigureAwait(false);

            histograms.Should().ContainSingle();
            histograms[0].Counts.Sum().Should().Be(2);
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task ExportImageAsync_WithColormap_ProducesRgbaImage()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreQueryTests));
        try
        {
            await CreateRasterTableAsync(schemaName);
            await CreateRasterStatisticsTableAsync(schemaName);
            var rasterId = await InsertFloatRasterAsync(schemaName);
            var store = new PostgresRasterStore(
                new FixtureConnectionProvider(fixture.DataSource),
                NullLogger<PostgresRasterStore>.Instance,
                FixtureBypassDatabaseSchemaGuard.Instance,
                schemaName);

            var result = await store.ExportImageAsync(
                    LayerId,
                    rasterId,
                    new RasterQuery
                    {
                        OutputFormat = RasterFormat.TIFF,
                        Colormap = new RasterColormap
                        {
                            Entries =
                            [
                                new RasterColormapEntry(0, 0, 0, 0, 255),
                                new RasterColormapEntry(30, 255, 255, 255, 255),
                            ],
                        },
                    })
                .ConfigureAwait(false);

            result.Data.Should().NotBeEmpty();

            // ST_ColorMap maps the single band to a 4-band RGBA image.
            var bandCount = await GetExportedBandCountAsync(schemaName, result.Data);
            bandCount.Should().Be(4);
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task ExportMosaicAsync_ByDateNewestOrdering_NewestRasterWinsOverlapPixel()
    {
        var (schemaName, ids) = await SeedMosaicStackAsync();
        try
        {
            var store = CreateStore(schemaName);

            // The west↔overlap-newest overlap column is x[1,2]; sample the pixel at (1.5, 1.5).
            // The newest acquisition (overlap-newest, value 5, 2024-02-01) must win.
            var winner = await ExportAndSampleOverlapPixelAsync(
                store, [ids.West, ids.OverlapNewest, ids.East],
                RasterMergeStrategy.Newest, RasterMosaicOrdering.AcquisitionNewest);

            winner.Should().Be(5);
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task ExportMosaicAsync_ByDateOldestOrdering_OldestRasterWinsOverlapPixel()
    {
        var (schemaName, ids) = await SeedMosaicStackAsync();
        try
        {
            var store = CreateStore(schemaName);

            // In the x[1,2] overlap, the oldest acquisition (west, value 20, 2024-01-01) must win.
            var winner = await ExportAndSampleOverlapPixelAsync(
                store, [ids.West, ids.OverlapNewest, ids.East],
                RasterMergeStrategy.Oldest, RasterMosaicOrdering.AcquisitionOldest);

            winner.Should().Be(20);
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task ExportMosaicAsync_NorthwestOrdering_UpperLeftMostRasterWinsOverlapPixel()
    {
        var (schemaName, ids) = await SeedMosaicStackAsync();
        try
        {
            var store = CreateStore(schemaName);

            // west and overlap-newest share the same YMax; west sits further west (XMin 0 vs 1)
            // so the Northwest ordering keeps west (value 20) in the overlap pixel.
            var winner = await ExportAndSampleOverlapPixelAsync(
                store, [ids.West, ids.OverlapNewest, ids.East],
                RasterMergeStrategy.Newest, RasterMosaicOrdering.Northwest);

            winner.Should().Be(20);
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task ExportMosaicAsync_LockOrder_OnlyLockedRastersContribute()
    {
        var (schemaName, ids) = await SeedMosaicStackAsync();
        try
        {
            var store = CreateStore(schemaName);

            // Lock to west + east only (drop overlap-newest). In the x[1,2] overlap pixel only
            // west (value 20) contributes, so the otherwise-winning overlap value (5) must not appear.
            var winner = await ExportAndSampleOverlapPixelAsync(
                store, [ids.West, ids.East],
                RasterMergeStrategy.Newest, RasterMosaicOrdering.LockOrder);

            winner.Should().Be(20);
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task ExportMosaicAsync_ByAttributeDescending_HighestAttributeRasterWinsOverlapPixel()
    {
        var (schemaName, ids) = await SeedMosaicStackAsync();
        try
        {
            var store = CreateStore(schemaName);

            // esriMosaicByAttribute over a non-date attribute (#1870): sort by the catalog "id"
            // column. west and overlap-newest are inserted first/second so overlap-newest has the
            // higher id. Descending (Esri default, highest value wins) keeps overlap-newest (5) in
            // the x[1,2] overlap pixel.
            var winner = await ExportAndSampleOverlapPixelAsync(
                store, [ids.West, ids.OverlapNewest, ids.East],
                RasterMergeStrategy.Newest, RasterMosaicOrdering.Attribute,
                new RasterMosaicAttributeSort("id", Ascending: false));

            winner.Should().Be(5);
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task ExportMosaicAsync_ByAttributeAscending_LowestAttributeRasterWinsOverlapPixel()
    {
        var (schemaName, ids) = await SeedMosaicStackAsync();
        try
        {
            var store = CreateStore(schemaName);

            // Ascending sort by "id": the lowest id (west) wins the x[1,2] overlap pixel (value 20).
            var winner = await ExportAndSampleOverlapPixelAsync(
                store, [ids.West, ids.OverlapNewest, ids.East],
                RasterMergeStrategy.Newest, RasterMosaicOrdering.Attribute,
                new RasterMosaicAttributeSort("id", Ascending: true));

            winner.Should().Be(20);
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task ExportMosaicAsync_NadirOrdering_LowestOffNadirRasterWinsOverlapPixel()
    {
        var (schemaName, ids) = await SeedMosaicStackAsync();
        try
        {
            await CreateSensorMetadataTableAsync(schemaName);

            // esriMosaicNadir (#1870): rank by off-nadir angle, lowest (closest to straight-down)
            // wins. Give west the most-nadir view (2 deg) and overlap-newest a steep off-nadir
            // (25 deg). Default newest-wins would keep overlap-newest (value 5) at the contested
            // pixel; the nadir ordering must instead keep west (value 20), proving off-nadir — not
            // acquisition — drives selection.
            await InsertSensorOffNadirAsync(schemaName, ids.West, offNadirAngle: 2);
            await InsertSensorOffNadirAsync(schemaName, ids.OverlapNewest, offNadirAngle: 25);

            var store = CreateStore(schemaName);

            var winner = await ExportAndSampleOverlapPixelAsync(
                store, [ids.West, ids.OverlapNewest, ids.East],
                RasterMergeStrategy.Newest, RasterMosaicOrdering.Nadir);

            winner.Should().Be(20, "the most-nadir raster wins the contested pixel");
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task ExportMosaicAsync_NadirOrdering_RasterWithoutSensorMetadataRanksLast()
    {
        var (schemaName, ids) = await SeedMosaicStackAsync();
        try
        {
            await CreateSensorMetadataTableAsync(schemaName);

            // Only overlap-newest carries an off-nadir angle; west has no sensor row (unknown
            // off-nadir). A known off-nadir must outrank an unknown one, so overlap-newest (value 5)
            // wins the contested pixel even though west would otherwise be selectable.
            await InsertSensorOffNadirAsync(schemaName, ids.OverlapNewest, offNadirAngle: 15);

            var store = CreateStore(schemaName);

            var winner = await ExportAndSampleOverlapPixelAsync(
                store, [ids.West, ids.OverlapNewest, ids.East],
                RasterMergeStrategy.Newest, RasterMosaicOrdering.Nadir);

            winner.Should().Be(5, "a raster with a known off-nadir angle outranks one with none");
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task ExportMosaicAsync_SeamlineOrdering_RasterClippedToSeamlineYieldsSeamWinner()
    {
        var (schemaName, ids) = await SeedMosaicStackAsync();
        try
        {
            await CreateFootprintsTableAsync(schemaName);

            // Default newest-wins gives 5 (overlap-newest) at the contested (1.5, 1.5) pixel.
            // Constrain overlap-newest's seamline to x[2,3] so it no longer covers the overlap
            // column; the seamline mosaic must then fall through to west (value 20).
            await InsertFootprintAsync(schemaName, ids.West, seamlineEnvelope: (0, 1, 2, 3));
            await InsertFootprintAsync(schemaName, ids.OverlapNewest, seamlineEnvelope: (2, 1, 3, 3));
            await InsertFootprintAsync(schemaName, ids.East, seamlineEnvelope: (2, 1, 4, 3));

            var store = CreateStore(schemaName);

            var winner = await ExportAndSampleOverlapPixelAsync(
                store, [ids.West, ids.OverlapNewest, ids.East],
                RasterMergeStrategy.Newest, RasterMosaicOrdering.Seamline);

            winner.Should().Be(20, "the seamline clips overlap-newest out of the contested column");
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task ExportMosaicAsync_SeamlineOrdering_MissingSeamlineContributesFullFootprint()
    {
        var (schemaName, ids) = await SeedMosaicStackAsync();
        try
        {
            await CreateFootprintsTableAsync(schemaName);
            // No footprint rows at all: every raster contributes its full pixels, so the seamline
            // ordering degrades to the default newest-wins result (5).
            var store = CreateStore(schemaName);

            var winner = await ExportAndSampleOverlapPixelAsync(
                store, [ids.West, ids.OverlapNewest, ids.East],
                RasterMergeStrategy.Newest, RasterMosaicOrdering.Seamline);

            winner.Should().Be(5, "with no seamline rows the newest raster still wins the overlap");
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task ExportImageAsync_WithSlopeTerrainFunction_ProducesSingleAnalyticBand()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreQueryTests));
        try
        {
            await CreateRasterTableAsync(schemaName);
            var rasterId = await InsertFloatRasterAsync(schemaName);
            var store = CreateStore(schemaName);

            // The 2x2 elevation gradient (0/10/20/30) yields a non-flat slope surface. The
            // inline terrain function must collapse it to one analytic band via ST_Slope.
            var result = await store.ExportImageAsync(
                    LayerId,
                    rasterId,
                    new RasterQuery
                    {
                        OutputFormat = RasterFormat.TIFF,
                        Terrain = new RasterTerrainFunction { Method = RasterTerrainMethod.Slope },
                    })
                .ConfigureAwait(false);

            result.Data.Should().NotBeEmpty();
            var bandCount = await GetExportedBandCountAsync(schemaName, result.Data);
            bandCount.Should().Be(1);
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    [IntegrationTest]
    public async Task ExportMosaicAsync_WithBandArithmeticNdvi_CollapsesToSingleAnalyticBand()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreQueryTests));
        try
        {
            await CreateRasterTableAsync(schemaName);
            // Two overlapping 2-band rasters so the mosaic path (not the single-raster path) runs.
            var first = await InsertTwoBandRasterAsync(schemaName, upperLeftX: 0, visible: 50, infrared: 200);
            var second = await InsertTwoBandRasterAsync(schemaName, upperLeftX: 1, visible: 60, infrared: 180);
            var store = CreateStore(schemaName);

            // NDVI over band1 (visible) and band2 (infrared): the mosaic export previously ignored
            // band math entirely (#1803). The output must be a single analytic band.
            var result = await store.ExportMosaicAsync(
                    LayerId,
                    [first, second],
                    RasterMergeStrategy.Newest,
                    new RasterQuery
                    {
                        OutputFormat = RasterFormat.TIFF,
                        BandArithmetic = new RasterBandArithmetic
                        {
                            VisibleBand = 1,
                            InfraredBand = 2,
                            Method = RasterBandArithmeticMethod.Ndvi,
                        },
                    })
                .ConfigureAwait(false);

            result.Data.Should().NotBeEmpty();
            var bandCount = await GetExportedBandCountAsync(schemaName, result.Data);
            bandCount.Should().Be(1);
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    private async Task<long> InsertTwoBandRasterAsync(
        string schemaName, double upperLeftX, double visible, double infrared)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO raster_data (layer_id, name, raster, acquisition_date, created_at)
            SELECT @layerId,
                   'two-band',
                   ST_AddBand(
                       ST_AddBand(
                           ST_MakeEmptyRaster(2, 2, @upperLeftX, 2, 1, -1, 0, 0, 4326),
                           '32BF'::text, @visible, NULL),
                       '32BF'::text, @infrared, NULL),
                   NOW(),
                   NOW()
            RETURNING id;
            """;
        command.Parameters.AddWithValue("layerId", LayerId);
        command.Parameters.AddWithValue("upperLeftX", upperLeftX);
        command.Parameters.AddWithValue("visible", visible);
        command.Parameters.AddWithValue("infrared", infrared);
        return (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    private PostgresRasterStore CreateStore(string schemaName)
        => new(
            new FixtureConnectionProvider(fixture.DataSource),
            NullLogger<PostgresRasterStore>.Instance,
            FixtureBypassDatabaseSchemaGuard.Instance,
            schemaName);

    private async Task CreateFootprintsTableAsync(string schemaName)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS raster_footprints (
                raster_data_id BIGINT PRIMARY KEY,
                footprint geometry NOT NULL,
                seamline geometry,
                srid INTEGER NOT NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at TIMESTAMPTZ
            );
            """;
        await command.ExecuteNonQueryAsync();
    }

    private async Task InsertFootprintAsync(
        string schemaName,
        long rasterId,
        (double MinX, double MinY, double MaxX, double MaxY) seamlineEnvelope)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO raster_footprints (raster_data_id, footprint, seamline, srid)
            VALUES (
                @rasterId,
                ST_MakeEnvelope(@minX, @minY, @maxX, @maxY, 4326),
                ST_MakeEnvelope(@minX, @minY, @maxX, @maxY, 4326),
                4326
            );
            """;
        command.Parameters.AddWithValue("rasterId", rasterId);
        command.Parameters.AddWithValue("minX", seamlineEnvelope.MinX);
        command.Parameters.AddWithValue("minY", seamlineEnvelope.MinY);
        command.Parameters.AddWithValue("maxX", seamlineEnvelope.MaxX);
        command.Parameters.AddWithValue("maxY", seamlineEnvelope.MaxY);
        await command.ExecuteNonQueryAsync();
    }

    private async Task CreateSensorMetadataTableAsync(string schemaName)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS raster_sensor_metadata (
                raster_data_id BIGINT PRIMARY KEY,
                sensor_name VARCHAR(255),
                camera_model VARCHAR(255),
                interior_orientation JSONB,
                exterior_orientation JSONB,
                rpc JSONB,
                dem_source VARCHAR(512),
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            """;
        await command.ExecuteNonQueryAsync();
    }

    private async Task InsertSensorOffNadirAsync(string schemaName, long rasterId, double offNadirAngle)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO raster_sensor_metadata (raster_data_id, exterior_orientation)
            VALUES (@rasterId, jsonb_build_object('offNadirAngle', @angle));
            """;
        command.Parameters.AddWithValue("rasterId", rasterId);
        command.Parameters.AddWithValue("angle", offNadirAngle);
        await command.ExecuteNonQueryAsync();
    }

    // Exports the full mosaic (no clip, so original raster envelopes drive Northwest ordering),
    // re-imports the GeoTIFF, and samples the contested pixel in the west↔overlap-newest overlap
    // column at world point (1.5, 1.5).
    private async Task<double> ExportAndSampleOverlapPixelAsync(
        PostgresRasterStore store,
        long[] rasterIds,
        RasterMergeStrategy mergeStrategy,
        RasterMosaicOrdering ordering,
        RasterMosaicAttributeSort? attributeSort = null)
    {
        var result = await store.ExportMosaicAsync(
                LayerId,
                rasterIds,
                mergeStrategy,
                new RasterQuery { OutputFormat = RasterFormat.TIFF },
                ordering,
                attributeSort)
            .ConfigureAwait(false);

        result.Data.Should().NotBeEmpty();
        return await SamplePixelAsync(_currentSchema!, result.Data, x: 1.5, y: 1.5);
    }

    private async Task<double> SamplePixelAsync(string schemaName, byte[] exportedRaster, double x, double y)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ST_Value(ST_FromGDALRaster(@data), 1, ST_SetSRID(ST_MakePoint(@x, @y), 4326));
            """;
        command.Parameters.AddWithValue("data", exportedRaster);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        return Convert.ToDouble(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private string? _currentSchema;

    private async Task<(string SchemaName, (long West, long OverlapNewest, long East) Ids)> SeedMosaicStackAsync()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreQueryTests));
        _currentSchema = schemaName;
        await CreateRasterTableAsync(schemaName);

        // Three 2x2 float rasters with offset extents and distinct acquisition dates, mirroring
        // the shared SeedIssue522MosaicAsync fixture: west [0,2] value 20 (oldest), overlap-newest
        // [1,3] value 5 (newest), east [2,4] value 40.
        var west = await InsertConstantRasterAsync(
            schemaName, "west", upperLeftX: 0, value: 20,
            acquisition: DateTimeOffset.Parse("2024-01-01T00:00:00Z", CultureInfo.InvariantCulture));
        var overlapNewest = await InsertConstantRasterAsync(
            schemaName, "overlap-newest", upperLeftX: 1, value: 5,
            acquisition: DateTimeOffset.Parse("2024-02-01T00:00:00Z", CultureInfo.InvariantCulture));
        var east = await InsertConstantRasterAsync(
            schemaName, "east", upperLeftX: 2, value: 40,
            acquisition: DateTimeOffset.Parse("2024-01-15T00:00:00Z", CultureInfo.InvariantCulture));

        return (schemaName, (west, overlapNewest, east));
    }

    private async Task<long> InsertConstantRasterAsync(
        string schemaName,
        string name,
        double upperLeftX,
        double value,
        DateTimeOffset acquisition)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO raster_data (layer_id, name, raster, acquisition_date, created_at)
            SELECT @layerId,
                   @name,
                   ST_AddBand(
                       ST_MakeEmptyRaster(2, 2, @upperLeftX, 2, 1, -1, 0, 0, 4326),
                       '32BF'::text,
                       @value,
                       NULL
                   ),
                   @acquisition,
                   @acquisition
            RETURNING id;
            """;
        command.Parameters.AddWithValue("layerId", LayerId);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("upperLeftX", upperLeftX);
        command.Parameters.AddWithValue("value", value);
        command.Parameters.AddWithValue("acquisition", acquisition.UtcDateTime);
        return (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    private async Task<int> GetExportedBandCountAsync(string schemaName, byte[] exportedRaster)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ST_NumBands(ST_FromGDALRaster(@data));";
        command.Parameters.AddWithValue("data", exportedRaster);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [IntegrationTest]
    public async Task ExportImageAsync_WithRenderingClip_MasksOutputToClipGeometry()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreQueryTests));
        try
        {
            await CreateRasterTableAsync(schemaName);
            var rasterId = await InsertFloatRasterAsync(schemaName);
            var store = new PostgresRasterStore(
                new FixtureConnectionProvider(fixture.DataSource),
                NullLogger<PostgresRasterStore>.Instance,
                FixtureBypassDatabaseSchemaGuard.Instance,
                schemaName);

            // Raster x[0,1] holds 0 (top) / 20 (bottom); x[1,2] holds 10 / 30.
            // A renderingRule Clip to the left column must keep only {0, 20}.
            var result = await store.ExportImageAsync(
                    LayerId,
                    rasterId,
                    new RasterQuery
                    {
                        OutputFormat = RasterFormat.TIFF,
                        RenderingClip = new RasterClipRegion
                        {
                            Geometry = CreateEnvelopeWkb(0, 0, 1, 2),
                            Srid = 4326,
                        },
                    })
                .ConfigureAwait(false);

            result.Data.Should().NotBeEmpty();

            var (count, min, max) = await SummarizeValidPixelsAsync(schemaName, result.Data);
            count.Should().Be(2);
            min.Should().Be(0);
            max.Should().Be(20);
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    // #4060: Esri clients paint exportImage pixels across the requested bbox, so a bbox reaching past
    // the raster must return an image covering the whole bbox with NoData outside the data.
    // Fixture: a 2x2 8BUI raster over x[0,10] y[0,10] (5-degree pixels, NoData 255) holding 1 2 / 3 4.
    // bbox [-10,10] at 40x40 gives 0.5-degree output pixels whose grid lines land on 0, so the data is
    // exactly the upper-right 20x20 pixels (400 valid) and the other three quadrants are NoData.
    [IntegrationTest]
    public async Task ExportImageAsync_WithCoverClipExtent_CoversRequestedBboxWithNoDataOutsideRaster()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreQueryTests));
        try
        {
            await CreateRasterTableAsync(schemaName);
            var rasterId = await InsertQuadrantRasterAsync(schemaName);
            var store = CreateStore(schemaName);
            var query = CreateCoverClipQuery(-10, -10, 10, 10, width: 40, height: 40);

            var result = await store.ExportImageAsync(LayerId, rasterId, query).ConfigureAwait(false);

            result.Width.Should().Be(40);
            result.Height.Should().Be(40);
            result.Srid.Should().Be(4326);
            result.Extent.Should().NotBeNull();
            result.Extent!.Value.XMin.Should().BeApproximately(-10, 1e-9);
            result.Extent.Value.YMin.Should().BeApproximately(-10, 1e-9);
            result.Extent.Value.XMax.Should().BeApproximately(10, 1e-9);
            result.Extent.Value.YMax.Should().BeApproximately(10, 1e-9);

            var probe = await ProbeExportedRasterAsync(
                schemaName,
                result.Data,
                srid: 4326,
                (2.25, 7.25), (7.25, 7.25), (2.25, 2.25), (7.25, 2.25),
                (-4.75, 4.75), (4.75, -4.75), (-4.75, -4.75));

            probe.Width.Should().Be(40);
            probe.Height.Should().Be(40);
            probe.XMin.Should().BeApproximately(-10, 1e-9);
            probe.YMin.Should().BeApproximately(-10, 1e-9);
            probe.XMax.Should().BeApproximately(10, 1e-9);
            probe.YMax.Should().BeApproximately(10, 1e-9);
            probe.NoData.Should().Be(255);
            probe.ValidPixels.Should().Be(400);
            probe.Values.Should().Equal(1, 2, 3, 4, null, null, null);

            // Without CoverClipExtent the store keeps trim semantics (WCS/Coverages): the output is
            // the bbox-raster intersection only.
            var trimmed = await store.ExportImageAsync(LayerId, rasterId, query with { CoverClipExtent = false }).ConfigureAwait(false);
            trimmed.Extent!.Value.XMin.Should().BeApproximately(0, 1e-9);
            trimmed.Extent.Value.YMin.Should().BeApproximately(0, 1e-9);
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    // #4060 with imageSR: the bbox is reprojected into the output SRID and the image covers that
    // envelope. Expected Web Mercator ordinates use the spherical closed form, independent of PostGIS.
    [IntegrationTest]
    public async Task ExportImageAsync_WithCoverClipExtentAndOutputSrid_CoversReprojectedBbox()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreQueryTests));
        try
        {
            await CreateRasterTableAsync(schemaName);
            var rasterId = await InsertQuadrantRasterAsync(schemaName);
            var store = CreateStore(schemaName);

            var result = await store.ExportImageAsync(
                    LayerId,
                    rasterId,
                    CreateCoverClipQuery(-10, -10, 10, 10, width: 64, height: 64) with { OutputSrid = 3857 })
                .ConfigureAwait(false);

            var (minX, minY) = ToWebMercator(-10, -10);
            var (maxX, maxY) = ToWebMercator(10, 10);
            var upperLeftData = ToWebMercator(2.5, 7.5);
            var lowerRightData = ToWebMercator(7.5, 2.5);
            var outsideData = ToWebMercator(-5, -5);

            result.Width.Should().Be(64);
            result.Height.Should().Be(64);
            result.Srid.Should().Be(3857);

            var probe = await ProbeExportedRasterAsync(
                schemaName, result.Data, srid: 3857, upperLeftData, lowerRightData, outsideData);

            probe.XMin.Should().BeApproximately(minX, 1e-3);
            probe.YMin.Should().BeApproximately(minY, 1e-3);
            probe.XMax.Should().BeApproximately(maxX, 1e-3);
            probe.YMax.Should().BeApproximately(maxY, 1e-3);

            // The data quadrant is exactly half of each axis (Mercator maps 0 to 0), so 32x32 pixels.
            probe.ValidPixels.Should().Be(32 * 32);
            probe.Values.Should().Equal(1, 4, null);
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    // #4060 on the mosaic path: west x[0,2] = 20, overlap-newest x[1,3] = 5 (newest), east x[2,4] = 40,
    // all y[0,2]. bbox [-4,-2,4,2] at 80x40 gives 0.1-degree pixels; the data covers x[0,4] y[0,2],
    // i.e. 40x20 = 800 valid pixels, and the newest raster wins the overlaps.
    [IntegrationTest]
    public async Task ExportMosaicAsync_WithCoverClipExtent_CoversRequestedBboxWithNoDataOutsideRasters()
    {
        var (schemaName, ids) = await SeedMosaicStackAsync();
        try
        {
            var store = CreateStore(schemaName);

            var result = await store.ExportMosaicAsync(
                    LayerId,
                    [ids.West, ids.OverlapNewest, ids.East],
                    RasterMergeStrategy.Newest,
                    CreateCoverClipQuery(-4, -2, 4, 2, width: 80, height: 40),
                    RasterMosaicOrdering.AcquisitionNewest)
                .ConfigureAwait(false);

            result.Width.Should().Be(80);
            result.Height.Should().Be(40);

            var probe = await ProbeExportedRasterAsync(
                schemaName,
                result.Data,
                srid: 4326,
                (0.55, 1.05), (1.55, 1.05), (2.55, 1.05), (3.55, 1.05),
                (-2.05, 1.05), (2.05, -1.05));

            probe.Width.Should().Be(80);
            probe.Height.Should().Be(40);
            probe.XMin.Should().BeApproximately(-4, 1e-9);
            probe.YMin.Should().BeApproximately(-2, 1e-9);
            probe.XMax.Should().BeApproximately(4, 1e-9);
            probe.YMax.Should().BeApproximately(2, 1e-9);
            probe.ValidPixels.Should().Be(800);
            probe.Values.Should().Equal(20, 5, 5, 40, null, null);
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    // #4061: an Esri start,end time extent selects the newest acquisition batch inside the window.
    [IntegrationTest]
    public async Task QueryRastersAsync_WithTimeExtent_SelectsNewestBatchInsideWindow()
    {
        var schemaName = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresRasterStoreQueryTests));
        try
        {
            await CreateRasterTableAsync(schemaName);
            await InsertRasterAsync(schemaName, "jan", DateTimeOffset.Parse("2024-01-01T00:00:00Z", CultureInfo.InvariantCulture), 0, 1);
            await InsertRasterAsync(schemaName, "feb", DateTimeOffset.Parse("2024-02-01T00:00:00Z", CultureInfo.InvariantCulture), 0, 1);
            await InsertRasterAsync(schemaName, "mar", DateTimeOffset.Parse("2024-03-01T00:00:00Z", CultureInfo.InvariantCulture), 0, 1);
            var store = CreateStore(schemaName);

            async Task<string[]> SelectAsync(string? start, string? end)
            {
                var rasters = await store.QueryRastersAsync(
                    LayerId,
                    new RasterSelectionQuery
                    {
                        TimeStart = start is null ? null : DateTimeOffset.Parse(start, CultureInfo.InvariantCulture),
                        Timestamp = end is null ? null : DateTimeOffset.Parse(end, CultureInfo.InvariantCulture)
                    }).ConfigureAwait(false);
                return rasters.Select(raster => raster.Name).ToArray();
            }

            (await SelectAsync("2024-01-15T00:00:00Z", "2024-02-15T00:00:00Z")).Should().Equal("feb");
            (await SelectAsync("2024-02-15T00:00:00Z", null)).Should().Equal("mar");
            (await SelectAsync(null, "2024-02-15T00:00:00Z")).Should().Equal("feb");
            (await SelectAsync("2024-01-02T00:00:00Z", "2024-01-20T00:00:00Z")).Should().BeEmpty(
                "no acquisition falls inside the window, so an older batch must not leak in");
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }

    private static RasterQuery CreateCoverClipQuery(double minX, double minY, double maxX, double maxY, int width, int height)
        => new()
        {
            OutputFormat = RasterFormat.TIFF,
            ClipRegion = new RasterClipRegion
            {
                Geometry = CreateEnvelopeWkb(minX, minY, maxX, maxY),
                Srid = 4326,
            },
            CoverClipExtent = true,
            OutputWidth = width,
            OutputHeight = height,
            ResamplingAlgorithm = ResamplingAlgorithm.NearestNeighbor,
        };

    private static (double X, double Y) ToWebMercator(double lon, double lat)
    {
        const double earthRadius = 6378137.0;
        return (
            lon * Math.PI / 180.0 * earthRadius,
            Math.Log(Math.Tan(Math.PI / 4.0 + lat * Math.PI / 360.0)) * earthRadius);
    }

    private sealed record ExportedRasterProbe(
        int Width,
        int Height,
        double XMin,
        double YMin,
        double XMax,
        double YMax,
        long ValidPixels,
        double? NoData,
        double?[] Values);

    private async Task<ExportedRasterProbe> ProbeExportedRasterAsync(
        string schemaName,
        byte[] exportedRaster,
        int srid,
        params (double X, double Y)[] points)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH decoded AS (SELECT ST_FromGDALRaster(@data, @srid) AS rast)
            SELECT ST_Width(rast),
                   ST_Height(rast),
                   ST_XMin(ST_Envelope(rast)),
                   ST_YMin(ST_Envelope(rast)),
                   ST_XMax(ST_Envelope(rast)),
                   ST_YMax(ST_Envelope(rast)),
                   COALESCE((ST_SummaryStats(rast, 1, true)).count, 0),
                   ST_BandNoDataValue(rast, 1),
                   ARRAY(
                       SELECT ST_Value(rast, 1, ST_SetSRID(ST_MakePoint(p.x, p.y), @srid))
                       FROM unnest(@xs, @ys) WITH ORDINALITY AS p(x, y, ord)
                       ORDER BY p.ord)
            FROM decoded;
            """;
        command.Parameters.AddWithValue("data", exportedRaster);
        command.Parameters.AddWithValue("srid", srid);
        command.Parameters.AddWithValue("xs", points.Select(point => point.X).ToArray());
        command.Parameters.AddWithValue("ys", points.Select(point => point.Y).ToArray());
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return new ExportedRasterProbe(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetDouble(2),
            reader.GetDouble(3),
            reader.GetDouble(4),
            reader.GetDouble(5),
            reader.GetInt64(6),
            reader.IsDBNull(7) ? null : reader.GetDouble(7),
            reader.GetFieldValue<double?[]>(8));
    }

    private async Task<long> InsertQuadrantRasterAsync(string schemaName)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO raster_data (layer_id, name, raster, acquisition_date, created_at)
            SELECT @layerId,
                   'quadrant',
                   ST_SetValues(
                       ST_AddBand(ST_MakeEmptyRaster(2, 2, 0, 10, 5, -5, 0, 0, 4326), '8BUI'::text, 0, 255),
                       1, 1, 1, ARRAY[[1, 2], [3, 4]]::double precision[][]),
                   NOW(),
                   NOW()
            RETURNING id;
            """;
        command.Parameters.AddWithValue("layerId", LayerId);
        return (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    private async Task<(long Count, double Min, double Max)> SummarizeValidPixelsAsync(string schemaName, byte[] exportedRaster)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (stats).count AS pixel_count, (stats).min AS min_value, (stats).max AS max_value
            FROM (SELECT ST_SummaryStats(ST_FromGDALRaster(@data), 1, true) AS stats) summarized;
            """;
        command.Parameters.AddWithValue("data", exportedRaster);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt64(0), reader.GetDouble(1), reader.GetDouble(2));
    }

    private async Task CreateRasterStatisticsTableAsync(string schemaName)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS raster_statistics (
                id BIGSERIAL PRIMARY KEY,
                raster_data_id BIGINT NOT NULL,
                band_number INTEGER NOT NULL,
                min_value DOUBLE PRECISION,
                max_value DOUBLE PRECISION,
                mean_value DOUBLE PRECISION,
                std_dev DOUBLE PRECISION,
                valid_pixel_count BIGINT,
                nodata_pixel_count BIGINT
            );
            """;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> InsertFloatRasterAsync(string schemaName)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO raster_data (layer_id, name, raster, acquisition_date, created_at)
            SELECT @layerId,
                   'float-stretch',
                   ST_SetValue(
                       ST_SetValue(
                           ST_SetValue(
                               ST_SetValue(
                                   ST_AddBand(
                                       ST_MakeEmptyRaster(2, 2, 0, 2, 1, -1, 0, 0, 4326),
                                       '32BF'::text, 0, NULL),
                                   1, 1, 1, 0::double precision),
                               1, 2, 1, 10::double precision),
                           1, 1, 2, 20::double precision),
                       1, 2, 2, 30::double precision),
                   NOW(),
                   NOW()
            RETURNING id;
            """;
        command.Parameters.AddWithValue("layerId", LayerId);
        return (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    private async Task<long> InsertImageServerProbeRasterAsync(string schemaName)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO raster_data (layer_id, name, raster, acquisition_date, created_at)
            SELECT @layerId,
                   'image-server-probe',
                   ST_AddBand(
                       ST_MakeEmptyRaster(64, 64, -123, 39, 0.03125, -0.03125, 0, 0, 4326),
                       '8BUI'::text,
                       7,
                       NULL
                   ),
                   NOW(),
                   NOW()
            RETURNING id;
            """;
        command.Parameters.AddWithValue("layerId", LayerId);
        return (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    private async Task<(string PixelType, double Min, double Max)> SummarizeExportedRasterAsync(
        string schemaName,
        byte[] exportedRaster)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ST_BandPixelType(rast, 1) AS pixel_type,
                   (stats).min AS min_value,
                   (stats).max AS max_value
            FROM (
                SELECT rast, ST_SummaryStats(rast, 1, false) AS stats
                FROM (SELECT ST_FromGDALRaster(@data) AS rast) decoded
            ) summarized;
            """;
        command.Parameters.AddWithValue("data", exportedRaster);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetString(0), reader.GetDouble(1), reader.GetDouble(2));
    }

    private async Task CreateRasterTableAsync(string schemaName)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS raster_data (
                id BIGSERIAL PRIMARY KEY,
                layer_id INTEGER NOT NULL,
                name VARCHAR(255) NOT NULL,
                raster raster NOT NULL,
                acquisition_date TIMESTAMPTZ,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at TIMESTAMPTZ,
                width INTEGER GENERATED ALWAYS AS (ST_Width(raster)) STORED,
                height INTEGER GENERATED ALWAYS AS (ST_Height(raster)) STORED,
                band_count INTEGER GENERATED ALWAYS AS (ST_NumBands(raster)) STORED,
                pixel_type VARCHAR(10) GENERATED ALWAYS AS (ST_BandPixelType(raster, 1)) STORED,
                srid INTEGER GENERATED ALWAYS AS (ST_SRID(raster)) STORED
            );
            """;
        await command.ExecuteNonQueryAsync();
    }

    private async Task InsertRasterAsync(
        string schemaName,
        string name,
        DateTimeOffset acquisitionDate,
        double upperLeftX,
        double upperLeftY)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO raster_data (layer_id, name, raster, acquisition_date, created_at)
            SELECT @layerId,
                   @name,
                   ST_AddBand(
                       ST_MakeEmptyRaster(1, 1, @upperLeftX, @upperLeftY, 1, -1, 0, 0, 4326),
                       '8BUI'::text,
                       7,
                       NULL
                   ),
                   @acquisitionDate,
                   @createdAt;
            """;
        command.Parameters.AddWithValue("layerId", LayerId);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("upperLeftX", upperLeftX);
        command.Parameters.AddWithValue("upperLeftY", upperLeftY);
        command.Parameters.AddWithValue("acquisitionDate", acquisitionDate.UtcDateTime);
        command.Parameters.AddWithValue("createdAt", acquisitionDate.UtcDateTime);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> InsertMultiBandRasterAsync(string schemaName)
    {
        await using var connection = await fixture.GetConnectionAsync(schemaName);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO raster_data (layer_id, name, raster, acquisition_date, created_at)
            SELECT @layerId,
                   'multi-band',
                   ST_AddBand(
                       ST_AddBand(
                           ST_MakeEmptyRaster(2, 2, 0, 2, 1, -1, 0, 0, 4326),
                           '8BUI'::text,
                           10,
                           NULL
                       ),
                       '8BUI'::text,
                       20,
                       NULL
                   ),
                   NOW(),
                   NOW()
            RETURNING id;
            """;
        command.Parameters.AddWithValue("layerId", LayerId);
        return (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    private static byte[] CreateEnvelopeWkb(double minX, double minY, double maxX, double maxY)
    {
        var factory = new GeometryFactory();
        return new WKBWriter().Write(factory.CreatePolygon(
        [
            new Coordinate(minX, minY),
            new Coordinate(maxX, minY),
            new Coordinate(maxX, maxY),
            new Coordinate(minX, maxY),
            new Coordinate(minX, minY)
        ]));
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
