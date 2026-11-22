// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

public sealed class FeatureServerNullSourceIdFixture : IAsyncLifetime
{
    public WebAppFixture App { get; } = new();

    public async Task InitializeAsync()
    {
        await App.InitializeAsync();
        App.RestoreV2ResourceSchemaFields(0,
        [
            new MetadataV2Field { Name = "id", Type = MetadataV2FieldType.Integer, Nullable = false },
            new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Nullable = true },
            new MetadataV2Field { Name = "shape", Type = MetadataV2FieldType.Geometry, Nullable = true }
        ]);
        var schema = App.CurrentSchema ?? throw new InvalidOperationException("Schema was not initialized.");
        await using var connection = await App.Postgres.GetConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO features (layer_id, objectid, attributes) VALUES
                (0, 900001, '{"id":null,"name":"null-source-id"}'),
                (0, 900002, '{"name":"missing-source-id"}'),
                (0, 900003, '{"id":123,"name":"present-source-id"}'),
                (0, 900004, '{"id":0,"name":"zero-source-id"}');
            """;
        await command.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => App.DisposeAsync();
}

[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerQueryNullSourceIdTests(FeatureServerNullSourceIdFixture fixture)
    : IClassFixture<FeatureServerNullSourceIdFixture>
{
    [IntegrationTheory]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [InlineData("json", "*")]
    [InlineData("json", "id,name")]
    [InlineData("geojson", "*")]
    [InlineData("geojson", "id,name")]
    public async Task Query_NullSourceId_EmitsNonNullDeclaredObjectId(string format, string outFields)
    {
        using var response = await fixture.App.Client.GetAsync(
            $"/rest/services/test/FeatureServer/0/query?objectIds=900001,900002&outFields={outFields}&returnGeometry=false&f={format}");
        var body = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(body);
        var features = document.RootElement.GetProperty("features").EnumerateArray().ToArray();
        features.Should().HaveCount(2, body);
        foreach (var feature in features)
        {
            var attributes = feature.GetProperty(format == "json" ? "attributes" : "properties");
            var expectedId = attributes.GetProperty("name").GetString() == "null-source-id" ? 900001L : 900002L;
            attributes.GetProperty("id").GetInt64().Should().Be(expectedId, body);
            if (format == "geojson")
            {
                feature.GetProperty("id").GetInt64().Should().Be(expectedId, body);
            }
        }
    }
}
