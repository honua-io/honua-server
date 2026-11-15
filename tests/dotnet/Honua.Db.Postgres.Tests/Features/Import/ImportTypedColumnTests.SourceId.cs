// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.Import;

public sealed partial class ImportTypedColumnTests
{
    [IntegrationTheory]
    [InlineData("id,name,lon,lat\n100,a,-157,21\n200,b,-156,21", "source_id")]
    [InlineData("ID,source_id,source_id_2,name,lon,lat\n100,300,500,a,-157,21\n200,400,600,b,-156,21", "source_id_3")]
    [InlineData("id,source_id,name,lon,lat\n100,,a,-157,21\n200,900,b,-156,21", "source_id_2")]
    [Operation(Operations.Import)]
    public async Task ImportFileAsync_CsvSourceId_PreservesQueryableAliasAndReportsRename(string csv, string alias)
    {
        var imported = await ImportAsync("csv_identity", "identity.csv", Encoding.UTF8.GetBytes(csv));
        imported.Result.TypedColumns.Select(column => column.Name).Should().Contain(alias);
        imported.Result.Warnings.Should().ContainSingle(warning => warning.Contains(alias, StringComparison.Ordinal));
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, "{alias}", name, COALESCE(properties->>'id', properties->>'ID')
            FROM "{imported.Schema}"."{imported.Table}"
            WHERE "{alias}" IN (100, 200)
            ORDER BY id
            """;
        await using var reader = await command.ExecuteReaderAsync();
        for (var generatedId = 1; generatedId <= 2; generatedId++)
        {
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetInt32(0).Should().Be(generatedId);
            reader.GetInt64(1).Should().Be(generatedId * 100L);
            reader.GetString(2).Should().Be(generatedId == 1 ? "a" : "b");
            reader.GetString(3).Should().Be((generatedId * 100).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        (await reader.ReadAsync()).Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.Import)]
    public async Task ImportFileAsync_DuplicateTextSourceIds_KeepsIndependentGeneratedKeys()
    {
        const string csv = "id,name,lon,lat\nexternal-a,a,-157,21\nexternal-a,b,-156,21";
        var imported = await ImportAsync("csv_text_identity", "identity.csv", Encoding.UTF8.GetBytes(csv));
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, source_id, properties->>'id'
            FROM "{imported.Schema}"."{imported.Table}"
            ORDER BY id
            """;
        await using var reader = await command.ExecuteReaderAsync();
        for (var generatedId = 1; generatedId <= 2; generatedId++)
        {
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetInt32(0).Should().Be(generatedId);
            reader.GetString(1).Should().Be("external-a");
            reader.GetString(2).Should().Be("external-a");
        }
        (await reader.ReadAsync()).Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.Import)]
    public async Task ImportFileAsync_LateCsvSourceId_ReservesQueryableColumnBeforeOptionalColumnLimit()
    {
        var optionalHeaders = Enumerable.Range(0, 128).Select(index => $"optional_{index}");
        var optionalValues = string.Join(",", Enumerable.Repeat("1", 128));
        var csv = $"id,{string.Join(",", optionalHeaders)},lon,lat\n,{optionalValues},-157,21\n200,{optionalValues},-156,21";
        var imported = await ImportAsync("csv_late_identity", "identity.csv", Encoding.UTF8.GetBytes(csv));
        imported.Result.TypedColumns.Select(column => column.Name).Should().Contain("source_id");
        imported.Result.Warnings.Should().ContainSingle(warning => warning.Contains("source_id", StringComparison.Ordinal));
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id FROM "{imported.Schema}"."{imported.Table}" WHERE source_id = 200
            """;
        (await command.ExecuteScalarAsync()).Should().Be(2);
    }

}
