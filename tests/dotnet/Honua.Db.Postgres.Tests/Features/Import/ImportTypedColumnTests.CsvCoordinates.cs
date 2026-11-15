// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Db.Postgres.Tests.Features.Import;

public sealed partial class ImportTypedColumnTests
{
    [IntegrationTest]
    [Operation(Operations.Import)]
    public async Task ImportFileAsync_CsvSemicolonDecimalComma_PersistsGeometry()
    {
        const string csv = """
            id;name;lon;lat
            1;München Marienplatz;11,575490;48,137393
            2;Köln Dom;6,958281;50,941278
            """;
        var imported = await ImportAsync("csv_decimal_comma", "places.csv", Encoding.UTF8.GetBytes(csv));
        try
        {
            imported.Result.FeatureCount.Should().Be(2);
            imported.Result.Warnings.Should().NotContain(warning => warning.Contains("WKT", StringComparison.OrdinalIgnoreCase));
            imported.Result.TypedColumns.Should().NotContain(column => column.Name == "lat" || column.Name == "lon");
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT name, ST_X(ST_GeometryN(geometry, 1)), ST_Y(ST_GeometryN(geometry, 1))
                FROM "{imported.Schema}"."{imported.Table}"
                ORDER BY id
                """;
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetString(0).Should().Be("München Marienplatz");
            reader.GetDouble(1).Should().BeApproximately(11.575490, 1e-6);
            reader.GetDouble(2).Should().BeApproximately(48.137393, 1e-6);
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetString(0).Should().Be("Köln Dom");
            reader.GetDouble(1).Should().BeApproximately(6.958281, 1e-6);
            reader.GetDouble(2).Should().BeApproximately(50.941278, 1e-6);
            (await reader.ReadAsync()).Should().BeFalse();
        }
        finally
        {
            await fixture.DropSchemaAsync(imported.Schema);
        }
    }
}
