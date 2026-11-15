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
            WHERE "{alias}" = 100
            """;
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt32(0).Should().Be(1);
        reader.GetInt64(1).Should().Be(100);
        reader.GetString(2).Should().Be("a");
        reader.GetString(3).Should().Be("100");
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

}
