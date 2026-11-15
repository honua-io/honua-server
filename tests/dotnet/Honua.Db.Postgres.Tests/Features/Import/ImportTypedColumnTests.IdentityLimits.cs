// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Crs;
using Honua.Db.Postgres.Features.FileImport;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Db.Postgres.Tests.Features.Import;

public sealed partial class ImportTypedColumnTests
{
    [IntegrationTheory]
    [InlineData("9223372036854775808", "9223372036854775809")]
    [InlineData("001", "1")]
    [InlineData("2020-01-01", "2020-01-01T00:00:00Z")]
    [Operation(Operations.Import)]
    public async Task ImportFileAsync_CsvIdentityLexemes_RemainDistinct(string first, string second)
    {
        var csv = $"id,lon,lat\n{first},-157,21\n{second},-156,21";
        var imported = await ImportAsync("csv_exact_identity", "identity.csv", Encoding.UTF8.GetBytes(csv));
        imported.Result.TypedColumns.Should().Contain(column => column.Name == "source_id" && column.PostgresType == "text");
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT source_id FROM \"{imported.Schema}\".\"{imported.Table}\" ORDER BY id";
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be(first);
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be(second);
        (await reader.ReadAsync()).Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.Import)]
    public async Task ImportFileAsync_AppendIdentityToFullTable_ReportsJsonPreservationWithoutClaimingColumn()
    {
        var headers = string.Join(",", Enumerable.Range(0, 128).Select(index => $"optional_{index}"));
        var values = string.Join(",", Enumerable.Repeat("1", 128));
        var imported = await ImportAsync("csv_full_identity", "full.csv", Encoding.UTF8.GetBytes($"{headers},lon,lat\n{values},-157,21"));
        imported.Result.TypedColumns.Should().HaveCount(128);
        var provider = new TestConnectionProvider(fixture.DataSource, imported.Schema);
        var service = new StreamingFileImportService(provider,
            new CrsDetectionService(provider, NullLogger<CrsDetectionService>.Instance),
            new FileFormatDetectionService(NullLogger<FileFormatDetectionService>.Instance),
            new NoopPerformanceMonitor(), NullLogger<StreamingFileImportService>.Instance,
            schemaConfiguration: ImportTestSchemaConfiguration.WithOperational(imported.Schema));
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("id,lon,lat\nexternal-a,-156,21"));
        var result = await service.ImportFileAsync(new ImportRequest
        {
            FileStream = stream, FileName = "identity.csv", TableName = "csv_full_identity",
            TargetSchema = imported.Schema, SourceSrid = 4326, TargetSrid = 4326, LoadMode = ImportLoadMode.Append
        });
        result.Success.Should().BeTrue(result.ErrorMessage);
        result.TypedColumns.Should().NotContain(column => column.Name == "source_id");
        result.Warnings.Should().Contain(warning => warning.Contains("could not be promoted", StringComparison.Ordinal));
        result.Warnings.Should().NotContain(warning => warning.Contains("is available as", StringComparison.Ordinal));
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT properties->>'source_id' FROM \"{imported.Schema}\".\"{imported.Table}\" WHERE id = 2";
        (await command.ExecuteScalarAsync()).Should().Be("external-a");
    }
}
