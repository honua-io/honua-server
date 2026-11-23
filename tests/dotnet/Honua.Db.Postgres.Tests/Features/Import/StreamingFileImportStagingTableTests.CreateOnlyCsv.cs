// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Import;

public sealed partial class StreamingFileImportStagingTableTests
{
    [IntegrationTest]
    public async Task ImportFileAsync_CreateOnlyCsv_CreatesNewDatasetWithReceipt()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("create_only_csv");
        try
        {
            await EnsureImportFunctionsAsync();
            var receiptWritten = false;

            var result = await ImportCreateOnlyCsvAsync(
                schema, "name,lon,lat\nA,-97.7,30.2\nB,-97.8,30.3\n", () => receiptWritten = true);

            result.Success.Should().BeTrue(result.ErrorMessage);
            result.FeatureCount.Should().Be(2);
            receiptWritten.Should().BeTrue();
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var count = new NpgsqlCommand(
                $"SELECT count(*) FROM \"{schema}\".\"{OverwriteGuardPhysicalTable}\" WHERE geometry IS NOT NULL", connection);
            (await count.ExecuteScalarAsync()).Should().Be(2L);
            (await StagingRelationsAsync(schema)).Should().BeEmpty();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTheory]
    [InlineData("name,wkt\nA,POINT(1 2)\nB,POINT(oops)\n")]
    [InlineData("name,lon,lat\nA,-97.7,30.2\nB,east,30.3\n")]
    public async Task ImportFileAsync_CreateOnlyCsvWithUnparseableGeometry_DiscardsDatasetWithoutReceipt(string csv)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("create_only_csv_bad");
        try
        {
            await EnsureImportFunctionsAsync();
            var receiptWritten = false;

            var result = await ImportCreateOnlyCsvAsync(schema, csv, () => receiptWritten = true);

            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Contain("1 row", "the agent is told how many rows to fix");
            receiptWritten.Should().BeFalse();
            (await TableExistsAsync(schema, OverwriteGuardPhysicalTable)).Should().BeFalse();
            (await StagingRelationsAsync(schema)).Should().BeEmpty();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private async Task<ImportResult> ImportCreateOnlyCsvAsync(string schema, string csv, Action onReceipt)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        return await CreateOnlyService(schema).ImportFileAsync(new ImportRequest
        {
            FileStream = stream,
            FileName = "points.csv",
            TableName = OverwriteGuardLogicalTable,
            TargetSchema = schema,
            SourceSrid = 4326,
            TargetSrid = 4326,
            LoadMode = ImportLoadMode.CreateOnly,
            CommitParticipant = (_, _) => { onReceipt(); return ValueTask.CompletedTask; },
        });
    }
}
