// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Server.Tests.Import;

public sealed partial class DirectImportCrsContractTests
{
    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task ImportFile_GeoJsonWithNullGeometry_StoresAllRowsAndWarns()
    {
        const string geoJson = """
            {"type":"FeatureCollection","features":[
              {"type":"Feature","geometry":{"type":"Point","coordinates":[1,2]},"properties":{"name":"first"}},
              {"type":"Feature","geometry":null,"properties":{"name":"unlocated"}},
              {"type":"Feature","geometry":{"type":"Point","coordinates":[3,4]},"properties":{"name":"third"}},
              {"type":"Feature","geometry":{"type":"Point","coordinates":[5,6]},"properties":{"name":"fourth"}}
            ]}
            """;
        using var result = await PostFileAsync("upload", new("mixed.geojson", geoJson, NewImportFields()));
        var imported = result.RootElement;
        imported.GetProperty("success").GetBoolean().Should().BeTrue(imported.GetRawText());
        var schema = imported.GetProperty("schema").GetString()!;
        var table = imported.GetProperty("physicalTableName").GetString()!;
        _importedTables.Add((schema, table));
        var warning = imported.GetProperty("validationErrors").EnumerateArray().Should().ContainSingle().Subject;
        warning.GetProperty("featureIndex").GetInt32().Should().Be(2);
        warning.GetProperty("field").GetString().Should().Be("geometry");
        warning.GetProperty("message").GetString().Should().Contain("null geometry");

        await using var connection = await _fixture.Postgres.GetConnectionAsync();
        await using var query = new NpgsqlCommand($"""
            SELECT count(*)::int, count(*) FILTER (WHERE geometry IS NULL)::int
            FROM {QualifiedTable(schema, table)}
            """, connection);
        await using var reader = await query.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt32(0).Should().Be(4);
        reader.GetInt32(1).Should().Be(1);
    }
}
