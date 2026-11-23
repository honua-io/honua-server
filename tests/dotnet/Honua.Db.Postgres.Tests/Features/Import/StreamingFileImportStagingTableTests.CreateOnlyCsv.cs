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

            var result = await ImportCreateOnlyCsvAsync(schema, new CreateOnlyCsvCase("name,lon,lat\nA,-97.7,30.2\nB,-97.8,30.3\n")
            {
                OnReceipt = () => receiptWritten = true,
            });

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
    [InlineData("name,wkt\nA,POINT(1 2)\nB,POINT(oops)\n", "1 row has an unparseable geometry")]
    [InlineData("name,lon,lat\nA,-97.7,30.2\nB,east,30.3\n", "1 row has an unparseable geometry")]
    [InlineData("name,lon,lat\nA,-97.7,30.2,extra\nB,-97.8,30.3\n", "1 row has more fields than the header row")]
    [InlineData("name,place\nA,Austin\n", "no geometry column was found")]
    public async Task ImportFileAsync_CreateOnlyIncompleteCsv_DiscardsDatasetWithoutReceipt(string csv, string expectedMessage)
    {
        await AssertCreateOnlyCsvRefusedAsync(new CreateOnlyCsvCase(csv), expectedMessage);
    }

    [IntegrationTest]
    public async Task ImportFileAsync_CreateOnlyCsvWithUngeocodedAddress_DiscardsDatasetWithoutReceipt()
    {
        var options = new CsvImportOptions
        {
            AddressColumn = "address",
            AddressGeocoder = (address, _) => Task.FromResult<CsvGeocodedAddress?>(
                address == "known" ? new CsvGeocodedAddress(-97.7, 30.2) : null),
        };

        await AssertCreateOnlyCsvRefusedAsync(
            new CreateOnlyCsvCase("name,address\nA,known\nB,nowhere\n") { Options = options },
            "1 row has an address that could not be geocoded (data rows 2)");
    }

    private async Task AssertCreateOnlyCsvRefusedAsync(CreateOnlyCsvCase csvCase, string expectedMessage)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("create_only_csv_bad");
        try
        {
            await EnsureImportFunctionsAsync();
            var receiptWritten = false;

            var result = await ImportCreateOnlyCsvAsync(schema, csvCase with { OnReceipt = () => receiptWritten = true });

            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Contain(expectedMessage).And.Contain("no dataset was created");
            receiptWritten.Should().BeFalse();
            (await TableExistsAsync(schema, OverwriteGuardPhysicalTable)).Should().BeFalse();
            (await StagingRelationsAsync(schema)).Should().BeEmpty();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private async Task<ImportResult> ImportCreateOnlyCsvAsync(string schema, CreateOnlyCsvCase csvCase)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csvCase.Csv));
        return await CreateOnlyService(schema).ImportFileAsync(new ImportRequest
        {
            FileStream = stream,
            FileName = "points.csv",
            TableName = OverwriteGuardLogicalTable,
            TargetSchema = schema,
            SourceSrid = 4326,
            TargetSrid = 4326,
            LoadMode = ImportLoadMode.CreateOnly,
            CsvOptions = csvCase.Options,
            CommitParticipant = (_, _) => { csvCase.OnReceipt?.Invoke(); return ValueTask.CompletedTask; },
        });
    }

    private sealed record CreateOnlyCsvCase(string Csv)
    {
        public CsvImportOptions? Options { get; init; }

        public Action? OnReceipt { get; init; }
    }
}
