// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
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
        var graph = App.GetCurrentV2GraphSnapshot().Graph;
        var publication = graph.Publications.First(candidate => candidate.LayerIndex == WebAppFixture.TestLayerId);
        var resource = graph.Resources.Single(candidate => candidate.Metadata.Id == publication.ResourceId);
        // Retain fields referenced by the seeded spatial and temporal metadata.
        App.RestoreV2ResourceSchemaFields(WebAppFixture.TestLayerId,
            resource.SchemaFields.Select(field => field.Name == "objectid"
                ? field with { Name = "id" }
                : field).ToArray());
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
        var where = Uri.EscapeDataString("name IN ('null-source-id','missing-source-id','present-source-id','zero-source-id')");
        using var response = await fixture.App.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?where={where}&outFields={outFields}&returnGeometry=false&f={format}");
        var body = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(body);
        document.RootElement.TryGetProperty("error", out _).Should().BeFalse(body);
        var features = document.RootElement.GetProperty("features").EnumerateArray().ToArray();
        features.Should().HaveCount(4, body);
        foreach (var feature in features)
        {
            var attributes = feature.GetProperty(format == "json" ? "attributes" : "properties");
            var expectedId = attributes.GetProperty("name").GetString() switch
            {
                "null-source-id" => 900001L,
                "missing-source-id" => 900002L,
                "present-source-id" => 123L,
                "zero-source-id" => 0L,
                var name => throw new InvalidOperationException($"Unexpected feature {name}.")
            };
            attributes.GetProperty("id").GetInt64().Should().Be(expectedId, body);
            if (format == "geojson")
            {
                feature.GetProperty("id").GetInt64().Should().Be(expectedId, body);
            }
        }
    }
}
