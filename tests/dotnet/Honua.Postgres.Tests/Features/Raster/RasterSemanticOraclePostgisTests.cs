// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.RasterSemantics;
using Npgsql;

namespace Honua.Postgres.Tests.Features.Raster;

/// <summary>Focused executable semantic evidence against every supported PostGIS CI version.</summary>
[Collection("Database")]
public sealed class RasterSemanticOraclePostgisTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task RuntimeVersion_IsInTheSupportedSemanticMatrix()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT postgis_lib_version(), postgis_raster_lib_version();";
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        var postgisVersion = reader.GetString(0);
        var rasterVersion = reader.GetString(1);
        Assert.Matches("^3\\.(4|5|6)(\\.|$)", postgisVersion);
        Assert.Matches("^3\\.(4|5|6)(\\.|$)", rasterVersion);
    }

    [IntegrationTest]
    public async Task StatisticsFixture_MatchesPopulationAndNoDataContract()
    {
        var fixtureDefinition = RasterSemanticFixtureCatalog.Load()
            .Single(candidate => candidate.Id == "statistics.nodata-population.v1");
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        var runtimeVersion = await GetRuntimeVersionAsync(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH empty AS (
                SELECT ST_AddBand(
                    ST_MakeEmptyRaster(2, 2, 0, 2, 1, -1, 0, 0, 4326),
                    '32BF', -9999, -9999) AS rast
            ), populated AS (
                SELECT ST_SetValue(
                    ST_SetValue(
                        ST_SetValue(rast, 1, 1, 1, 1),
                        1, 2, 1, 2),
                    1, 1, 2, 3) AS rast
                FROM empty
            )
            SELECT (summary).count,
                   (summary).min,
                   (summary).max,
                   (summary).mean,
                   (summary).stddev
            FROM (
                SELECT ST_SummaryStats(rast, 1, TRUE) AS summary
                FROM populated
            ) AS stats;
            """;
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        var actual = new RasterSemanticSnapshot
        {
            Scalars = new Dictionary<string, double?>(StringComparer.Ordinal)
            {
                ["band.1.count"] = reader.GetInt64(0),
                ["band.1.min"] = reader.GetDouble(1),
                ["band.1.max"] = reader.GetDouble(2),
                ["band.1.mean"] = reader.GetDouble(3),
                ["band.1.stddev"] = reader.GetDouble(4),
            },
        };

        var comparison = RasterSemanticOracle.Compare(fixtureDefinition, new RasterSemanticObservation
        {
            ProcessId = fixtureDefinition.ProcessId,
            SemanticVersion = fixtureDefinition.SemanticVersion,
            Engine = "postgis",
            ImplementationVersion = "honua.postgis.raster.statistics@1.0.0",
            RuntimeVersion = runtimeVersion,
            Outcome = RasterSemanticOutcome.Success,
            Snapshot = actual,
        });
        Assert.True(comparison.IsMatch, FormatDifferences(comparison));
    }

    [IntegrationTest]
    public async Task SlopeFixture_MatchesGridNoDataAndHornInteriorContract()
    {
        var fixtureDefinition = RasterSemanticFixtureCatalog.Load()
            .Single(candidate => candidate.Id == "surface.slope-plane-degrees.v1");
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        var runtimeVersion = await GetRuntimeVersionAsync(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH empty AS (
                SELECT ST_AddBand(
                    ST_MakeEmptyRaster(3, 3, 500000, 2200000, 1, -1, 0, 0, 32604),
                    '32BF', -9999, -9999) AS rast
            ), populated AS (
                SELECT ST_SetValue(ST_SetValue(ST_SetValue(
                       ST_SetValue(ST_SetValue(ST_SetValue(
                       ST_SetValue(ST_SetValue(ST_SetValue(
                           rast,
                           1, 1, 1, 0), 1, 2, 1, 1), 1, 3, 1, 2),
                           1, 1, 2, 0), 1, 2, 2, 1), 1, 3, 2, 2),
                           1, 1, 3, 0), 1, 2, 3, 1), 1, 3, 3, 2) AS rast
                FROM empty
            ), result AS (
                SELECT ST_Slope(rast, 1, '32BF', 'DEGREES', 1, FALSE) AS rast
                FROM populated
            )
            SELECT ST_Width(rast),
                   ST_Height(rast),
                   ST_SRID(rast),
                   ST_UpperLeftX(rast),
                   ST_PixelWidth(rast),
                   ST_SkewX(rast),
                   ST_UpperLeftY(rast),
                   ST_SkewY(rast),
                   ST_PixelHeight(rast),
                   ST_BandPixelType(rast, 1),
                   ST_BandNoDataValue(rast, 1),
                   x,
                   y,
                   ST_Value(rast, 1, x, y, FALSE)
            FROM result
            CROSS JOIN generate_series(1, 3) AS y
            CROSS JOIN generate_series(1, 3) AS x
            ORDER BY y, x;
            """;
        await using var reader = await command.ExecuteReaderAsync();

        RasterSemanticGrid? grid = null;
        string? pixelType = null;
        double? noData = null;
        var cells = new List<double?>();
        while (await reader.ReadAsync())
        {
            grid ??= new RasterSemanticGrid
            {
                Width = reader.GetInt32(0),
                Height = reader.GetInt32(1),
                Srid = reader.GetInt32(2),
                Transform =
                [
                    reader.GetDouble(3),
                    reader.GetDouble(4),
                    reader.GetDouble(5),
                    reader.GetDouble(6),
                    reader.GetDouble(7),
                    reader.GetDouble(8),
                ],
            };
            pixelType ??= reader.GetString(9);
            noData ??= reader.GetDouble(10);
            cells.Add(reader.IsDBNull(13) ? null : reader.GetDouble(13));
        }

        Assert.NotNull(grid);
        var actual = new RasterSemanticSnapshot
        {
            Grid = grid,
            Bands =
            [
                new RasterSemanticBand
                {
                    PixelType = pixelType!,
                    ColorInterpretation = "gray",
                    NoData = noData,
                    Cells = cells,
                },
            ],
        };
        var comparison = RasterSemanticOracle.Compare(fixtureDefinition, new RasterSemanticObservation
        {
            ProcessId = fixtureDefinition.ProcessId,
            SemanticVersion = fixtureDefinition.SemanticVersion,
            Engine = "postgis",
            ImplementationVersion = "honua.postgis.surface.slope@1.0.0",
            RuntimeVersion = runtimeVersion,
            Outcome = RasterSemanticOutcome.Success,
            Snapshot = actual,
        });
        Assert.True(comparison.IsMatch, FormatDifferences(comparison));
    }

    [IntegrationTest]
    public async Task CancellationFixture_InterruptsDatabaseWorkWithoutAResult()
    {
        var fixtureDefinition = RasterSemanticFixtureCatalog.Load()
            .Single(candidate => candidate.Id == "mosaic.cancellation.v1");
        Assert.Equal(RasterSemanticOutcome.Cancelled, fixtureDefinition.Outcome);

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        var runtimeVersion = await GetRuntimeVersionAsync(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH raster_work AS (
                SELECT ST_Union(
                    ST_AddBand(ST_MakeEmptyRaster(64, 64, 0, 64, 1, -1, 0, 0, 4326), '8BUI', i, 0),
                    'LAST') AS rast
                FROM generate_series(1, 5000) AS i
            )
            SELECT pg_sleep(30), ST_Width(rast)
            FROM raster_work;
            """;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await command.ExecuteScalarAsync(cancellation.Token));

        var comparison = RasterSemanticOracle.Compare(fixtureDefinition, new RasterSemanticObservation
        {
            ProcessId = fixtureDefinition.ProcessId,
            SemanticVersion = fixtureDefinition.SemanticVersion,
            Engine = "postgis",
            ImplementationVersion = "honua.postgis.raster.mosaic@1.0.0",
            RuntimeVersion = runtimeVersion,
            Outcome = RasterSemanticOutcome.Cancelled,
        });
        Assert.True(comparison.IsMatch, FormatDifferences(comparison));
    }

    private static async Task<string> GetRuntimeVersionAsync(NpgsqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT postgis_lib_version();";
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static string FormatDifferences(RasterSemanticComparison comparison) => string.Join(
        Environment.NewLine,
        comparison.Differences.Select(difference => $"{difference.Path}: {difference.Message}"));
}
