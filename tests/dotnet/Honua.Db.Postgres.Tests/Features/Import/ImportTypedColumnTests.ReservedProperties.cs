// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Db.Postgres.Tests.Features.Import;

public sealed partial class ImportTypedColumnTests
{
    [IntegrationTheory]
    [InlineData("id", "src-101")]
    [InlineData("geometry", "text-geom-a")]
    [InlineData("ID", "src-101")]
    [InlineData("Geometry", "text-geom-a")]
    [Operation(Operations.Import)]
    public async Task ImportFileAsync_ReservedSourceProperty_ReportsHiddenValueAndKeepsOriginal(
        string name, string value)
    {
        var json = $$$"""
            {"type":"FeatureCollection","features":[
              {"type":"Feature","geometry":{"type":"Point","coordinates":[1,2]},
               "properties":{"{{{name}}}":"{{{value}}}","name":"row0"}},
              {"type":"Feature","geometry":{"type":"Point","coordinates":[3,4]},
               "properties":{"{{{name}}}":"{{{value}}}","name":"row1"}}]}
            """;
        var imported = await ImportAsync("reserved_source", "reserved.geojson", Encoding.UTF8.GetBytes(json));

        imported.Result.Warnings.Should().ContainSingle(warning =>
            warning.Contains($"'{name}'", StringComparison.Ordinal)
            && warning.Contains("source_" + name.ToLowerInvariant(), StringComparison.Ordinal)
            && warning.Contains("published", StringComparison.Ordinal));
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, ST_AsText(geometry), properties->>@name
            FROM "{imported.Schema}"."{imported.Table}" ORDER BY id
            """;
        command.Parameters.AddWithValue("name", name);
        await using var reader = await command.ExecuteReaderAsync();
        for (var id = 1; id <= 2; id++)
        {
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetInt32(0).Should().Be(id);
            reader.GetString(1).Should().Be(id == 1 ? "POINT(1 2)" : "POINT(3 4)");
            reader.GetString(2).Should().Be(value);
        }
        (await reader.ReadAsync()).Should().BeFalse();
    }
}
