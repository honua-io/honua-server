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
    public async Task ImportFileAsync_NonAsciiPropertyKeys_WarnsPerKeyAndKeepsValues()
    {
        const string json = """
            {"type":"FeatureCollection","features":[
              {"type":"Feature","geometry":{"type":"Point","coordinates":[1,2]},
               "properties":{"sid":"A1","name":"Honolulu","ʻāina":"mokupuni","名前":"名-A1","länge_straße":"x"}},
              {"type":"Feature","geometry":{"type":"Point","coordinates":[3,4]},
               "properties":{"sid":"A2","name":"Hilo","ʻāina":"moku","名前":"名-A2","länge_straße":"y"}}]}
            """;
        var imported = await ImportAsync("unicode_keys", "unicode.geojson", Encoding.UTF8.GetBytes(json));

        imported.Result.TypedColumns.Select(column => column.Name).Should().BeEquivalentTo("sid", "name");
        foreach (var key in new[] { "ʻāina", "名前", "länge_straße" })
        {
            imported.Result.Warnings.Should().ContainSingle(warning =>
                warning.Contains($"'{key}'", StringComparison.Ordinal)
                && warning.Contains("hidden in published features", StringComparison.Ordinal));
        }

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT properties->>'名前' FROM "{imported.Schema}"."{imported.Table}" ORDER BY id
            """;
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be("名-A1");
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be("名-A2");
    }
}
