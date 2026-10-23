// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Db.Postgres.Features.Raster;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.Raster;

/// <summary>
/// The sampler functions are the planning boundary for corrected nearest SQL.
/// </summary>
[Collection("Database")]
public sealed class RasterSamplingFunctionsIntegrationTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task InstalledFunctions_MatchThePackagedBodiesAndAreNotInlined()
    {
        await using var connection = await fixture.GetConnectionAsync();
        await RasterSamplingSql.VerifyFunctionsAsync(connection);
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TEMP TABLE sampler_boundary_source (rast raster) ON COMMIT DROP;
            CREATE TEMP TABLE sampler_boundary_grid (rast raster) ON COMMIT DROP;
            INSERT INTO sampler_boundary_source
            SELECT ST_AddBand(ST_MakeEmptyRaster(1, 1, 0, 1, 1, -1, 0, 0, 4326), '8BUI'::text, 7, 255);
            INSERT INTO sampler_boundary_grid
            SELECT ST_MakeEmptyRaster(1, 1, 0, 1, 1, -1, 0, 0, 4326);
            """;
        await command.ExecuteNonQueryAsync();

        var sample = RasterCorrectedNearestSql.Sample("s.rast", "g.rast");
        command.CommandText = $"""
            EXPLAIN (VERBOSE, FORMAT JSON)
            SELECT {sample}
            FROM sampler_boundary_source s, sampler_boundary_grid g;
            """;
        var plan = (string)(await command.ExecuteScalarAsync())!;
        plan.Should().Contain("honua_raster");
        plan.Should().NotContain("window_input");
        plan.Should().NotContain("sample_input");
        plan.Should().NotContain("nearest_input");

        command.CommandText = $"""
            SELECT ST_Value({sample}, 1, 1, 1, FALSE)
            FROM sampler_boundary_source s, sampler_boundary_grid g;
            """;
        ((double)(await command.ExecuteScalarAsync())!).Should().Be(7);
        await transaction.RollbackAsync();
    }
}
