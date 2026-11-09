// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Raster.Domain;
using Honua.Db.Postgres.Features.Raster;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.Raster;

public sealed partial class PostgresRasterStoreUnalignedMosaicTests
{
    [IntegrationTest]
    public async Task CreateIdentifyContributorArraySql_UnalignedInputs_BoundsCompilationWork()
    {
        const int MaximumCompiledFunctions = 512;
        var schemaName = await CreateSchemaAsync();
        try
        {
            var signed = await InsertConstantRasterAsync(schemaName, "signed", 0, 2, 1, -1, Day(1));
            var unsigned = await InsertUnsignedRasterAsync(schemaName, "8BUI", 255);
            await using var connection = await fixture.GetConnectionAsync(schemaName);
            await using var transaction = await connection.BeginTransactionAsync();
            await using var settings = connection.CreateCommand();
            settings.Transaction = transaction;
            settings.CommandText = "SET LOCAL jit = on";
            await settings.ExecuteNonQueryAsync();
            var contributors = RasterMosaicSql.CreateIdentifyContributorArraySql(RasterMergeStrategy.Newest,
                RasterMosaicOrdering.AcquisitionNewest, null, "ST_SetSRID(ST_MakePoint(2.5, 1.5), 4326)");
            var query = $"""
                WITH source AS MATERIALIZED (
                    SELECT raster AS rast, id, created_at,
                           COALESCE(acquisition_date, created_at) AS effective_acquisition
                    FROM raster_data WHERE layer_id = @layerId AND id = ANY(@ids)
                )
                SELECT {contributors};
                """;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.Parameters.AddWithValue("layerId", LayerId);
            command.Parameters.AddWithValue("ids", new[] { signed, unsigned });
            command.CommandText = "EXPLAIN (ANALYZE, FORMAT JSON) " + query;
            using var plan = JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!);
            var summary = plan.RootElement[0];
            var functions = summary.TryGetProperty("JIT", out var jit) ? jit.GetProperty("Functions").GetInt32() : 0;
            var output = RepositoryPaths.Resolve("artifacts", "raster-investigation");
            Directory.CreateDirectory(output);
            var metrics = FormattableString.Invariant(
                $"{{\"CompiledFunctions\":{functions},\"PlanningMilliseconds\":{summary.GetProperty("Planning Time").GetDouble()},\"ExecutionMilliseconds\":{summary.GetProperty("Execution Time").GetDouble()}}}");
            await File.WriteAllTextAsync(Path.Combine(output, "contributor-summary.json"), metrics);
            Assert.True(functions <= MaximumCompiledFunctions,
                $"Compilation budget exceeded: {functions} > {MaximumCompiledFunctions}");
            command.CommandText = query;
            Assert.Equal(new[] { unsigned }, (long[])(await command.ExecuteScalarAsync())!);
            await transaction.RollbackAsync();
        }
        finally
        {
            await fixture.DropSchemaAsync(schemaName);
        }
    }
}
