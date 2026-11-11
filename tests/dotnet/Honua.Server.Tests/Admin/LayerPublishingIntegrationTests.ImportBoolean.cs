// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Admin;

public sealed partial class LayerPublishingIntegrationTests
{
    [IntegrationTheory]
    [InlineData(0, "Bravo")]
    [InlineData(1, "Alpha")]
    [Protocol(TestProtocols.FeatureServer, TestProtocols.Mcp)]
    [Operation(Operations.Import)]
    [Operation(Operations.Create)]
    [Operation(Operations.Query)]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /api/v1/admin/import/upload")]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [Endpoint("POST /mcp tools/call honua_query_features")]
    public async Task PublishImportedBoolean_NumericFilterWorksInRestAndMcp(int value, string expectedName)
    {
        const string source = """
            {"type":"FeatureCollection","features":[
              {"type":"Feature","geometry":{"type":"Point","coordinates":[0,0]},"properties":{"is_demo":true,"name":"Alpha"}},
              {"type":"Feature","geometry":{"type":"Point","coordinates":[1,1]},"properties":{"is_demo":false,"name":"Bravo"}}
            ]}
            """;
        await ImportAndPublishAttributesAsync(source, ["id", "is_demo", "name", "properties"]);
        using var metadata = await GetGeometryJsonAsync($"/rest/services/{_serviceName}/FeatureServer/{_layerId}?f=json");
        metadata.RootElement.GetProperty("fields").EnumerateArray().Single(field =>
            field.GetProperty("name").GetString() == "is_demo").GetProperty("type").GetString().Should().Be("esriFieldTypeSmallInteger");
        var where = $"is_demo={value}";
        using var rest = JsonDocument.Parse(await ReadFeatureServerAsync(where, "id ASC"));
        var attributes = rest.RootElement.GetProperty("features").EnumerateArray().Should().ContainSingle().Subject.GetProperty("attributes");
        attributes.GetProperty("name").GetString().Should().Be(expectedName);
        attributes.GetProperty("is_demo").GetInt32().Should().Be(value);
        var mcp = await CallGeometryCatalogAsync("honua_query_features", new
        {
            serviceId = _serviceName,
            layerId = _layerId,
            where,
            outFields = new[] { "id", "is_demo", "name" },
            limit = 10
        });
        mcp.GetProperty("features").EnumerateArray().Should().ContainSingle().Subject.GetProperty("attributes")
            .GetProperty("name").GetString().Should().Be(expectedName);
    }
}
