// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.FeatureServer;

/// <summary>
/// The FeatureServer service root must list the relationship classes its layers take
/// part in. ArcGIS Pro enables the attribute table's Related Data command only when the
/// service root advertises the relationship; with a layer-only listing it offered
/// "None available" (honua-esri-compat native-pro-matrix-20260917-a, UI-FEAT-RELATIONSHIPS).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.Metadata)]
public sealed class FeatureServerServiceRootRelationshipsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    public async Task ServiceRoot_ListsTheRelationshipsItsLayersDeclare()
    {
        using var serviceResponse = await _fixture.Client.GetAsync($"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer?f=json");
        serviceResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var service = JsonDocument.Parse(await serviceResponse.Content.ReadAsStringAsync());

        var layerIds = service.RootElement.GetProperty("layers").EnumerateArray()
            .Select(layer => layer.GetProperty("id").GetInt32())
            .ToHashSet();
        var relationships = service.RootElement.GetProperty("relationships").EnumerateArray().ToArray();

        // The seeded test layer declares "Test Relationship" (esri id 1) to layer 1.
        using var layerResponse = await _fixture.Client.GetAsync($"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}?f=json");
        using var layer = JsonDocument.Parse(await layerResponse.Content.ReadAsStringAsync());
        var declared = layer.RootElement.GetProperty("relationships").EnumerateArray()
            .Where(r => layerIds.Contains(r.GetProperty("relatedTableId").GetInt32()))
            .Select(r => r.GetProperty("id").GetInt32())
            .ToArray();
        declared.Should().NotBeEmpty("the fixture seeds a relationship from the test layer to a published layer");

        relationships.Select(r => r.GetProperty("id").GetInt32()).Should().Contain(declared);
        relationships.Should().OnlyContain(
            r => layerIds.Contains(r.GetProperty("relatedTableId").GetInt32()),
            "the service root lists only relationships whose related layer is visible in the service");
        relationships.Select(r => r.GetProperty("id").GetInt32()).Should().OnlyHaveUniqueItems();
        relationships.Should().Contain(r => r.GetProperty("id").GetInt32() == 1 && r.GetProperty("name").GetString() == "Test Relationship");
    }
}
