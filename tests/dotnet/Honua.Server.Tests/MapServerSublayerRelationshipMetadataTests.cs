// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests;

/// <summary>
/// Regression tests for #1923: a MapServer sublayer's metadata must surface the same
/// <c>relationships</c> and <c>hasAttachments</c> that the equivalent FeatureServer layer
/// advertises, so ArcGIS clients consuming the MapServer can discover related records and
/// attachments. The seeded test layer 0 declares a relationship class and opts into
/// attachments, so both protocol surfaces are expected to report them.
/// </summary>
[Protocol(TestProtocols.MapServer)]
[Collection("Database.CoreFeatureStore")]
public sealed class MapServerSublayerRelationshipMetadataTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);
    private const string TestServiceId = "test";
    private const int TestLayerId = 0;

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task FeatureServerLayerMetadata_SurfacesRelationshipsAndHasAttachments()
    {
        using var document = await GetLayerMetadataAsync("FeatureServer");
        var root = document.RootElement;

        AssertRelationshipsPresent(root);
        root.GetProperty("hasAttachments").GetBoolean().Should().BeTrue(
            "the seeded test layer opts into attachments");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/{layerId}")]
    public async Task MapServerSublayerMetadata_SurfacesRelationshipsAndHasAttachments()
    {
        // The core of #1923: the MapServer sublayer previously emitted relationships:[] and
        // hasAttachments:false even though the same layer's FeatureServer metadata advertised
        // both. Assert the MapServer sublayer now mirrors the underlying layer.
        using var document = await GetLayerMetadataAsync("MapServer");
        var root = document.RootElement;

        AssertRelationshipsPresent(root);
        root.GetProperty("hasAttachments").GetBoolean().Should().BeTrue(
            "the MapServer sublayer must mirror the FeatureServer layer's hasAttachments (#1923)");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/{layerId}")]
    public async Task MapServerSublayerMetadata_MatchesFeatureServerRelationshipsAndAttachments()
    {
        // Both protocol surfaces must agree on the relationship ids and attachment support so
        // a client consuming either surface sees a consistent layer description.
        using var featureServer = await GetLayerMetadataAsync("FeatureServer");
        using var mapServer = await GetLayerMetadataAsync("MapServer");

        var featureRelationshipIds = ReadRelationshipIds(featureServer.RootElement);
        var mapRelationshipIds = ReadRelationshipIds(mapServer.RootElement);

        mapRelationshipIds.Should().BeEquivalentTo(featureRelationshipIds,
            "the MapServer sublayer relationships must mirror the FeatureServer layer relationships");

        mapServer.RootElement.GetProperty("hasAttachments").GetBoolean()
            .Should().Be(featureServer.RootElement.GetProperty("hasAttachments").GetBoolean());
    }

    private static void AssertRelationshipsPresent(JsonElement root)
    {
        root.TryGetProperty("relationships", out var relationships).Should().BeTrue(
            "layer metadata must include a relationships array");
        relationships.ValueKind.Should().Be(JsonValueKind.Array);
        relationships.GetArrayLength().Should().BeGreaterThan(0,
            "the seeded test layer declares at least one relationship");

        foreach (var relationship in relationships.EnumerateArray())
        {
            relationship.GetProperty("id").ValueKind.Should().Be(JsonValueKind.Number);
            relationship.GetProperty("relatedTableId").ValueKind.Should().Be(JsonValueKind.Number);
            relationship.GetProperty("name").ValueKind.Should().Be(JsonValueKind.String);
        }
    }

    private static List<int> ReadRelationshipIds(JsonElement root)
    {
        var ids = new List<int>();
        if (root.TryGetProperty("relationships", out var relationships)
            && relationships.ValueKind == JsonValueKind.Array)
        {
            foreach (var relationship in relationships.EnumerateArray())
            {
                ids.Add(relationship.GetProperty("id").GetInt32());
            }
        }

        ids.Sort();
        return ids;
    }

    private async Task<JsonDocument> GetLayerMetadataAsync(string serviceType)
    {
        using var client = _fixture.CreateClient();
        var response = await client.GetAsync(
            $"/rest/services/{TestServiceId}/{serviceType}/{TestLayerId}?f=json");
        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(content);
    }
}
