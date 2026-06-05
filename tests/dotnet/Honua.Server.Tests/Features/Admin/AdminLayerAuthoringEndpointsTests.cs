// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Integration tests for the admin layer-authoring endpoints (popup-info, drawing-info, and
/// relationships) that write into the Metadata v2 graph and are read back by the GeoServices /
/// OGC / OData emitters.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class AdminLayerAuthoringEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/popup-info")]
    [Endpoint("GET /api/v1/admin/metadata/layers/{layerId}/popup-info")]
    public async Task PutPopupInfo_ThenGet_RoundTripsStoredDocument()
    {
        var client = _fixture.CreateAdminClient();
        var document = JsonSerializer.Deserialize<JsonElement>("""
        {
            "title": "{name}",
            "description": "Population: {population}"
        }
        """);

        var putResponse = await client.PutAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/popup-info",
            JsonContent.Create(document, LayerAuthoringJsonContext.Default.JsonElement));
        putResponse.Be200Ok();

        var put = await DeserializeDocumentAsync(putResponse);
        put!.Success.Should().BeTrue();
        put.Data.Should().NotBeNull();
        put.Data!.LayerId.Should().Be(WebAppFixture.TestLayerId);
        put.Data.Document!.Value.GetProperty("title").GetString().Should().Be("{name}");

        var getResponse = await client.GetAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/popup-info");
        getResponse.Be200Ok();

        var get = await DeserializeDocumentAsync(getResponse);
        get!.Data.Should().NotBeNull();
        get.Data!.Document.HasValue.Should().BeTrue();
        get.Data.Document!.Value.GetProperty("description").GetString().Should().Be("Population: {population}");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/popup-info")]
    public async Task PutPopupInfo_WithNonObjectBody_ReturnsBadRequest()
    {
        var client = _fixture.CreateAdminClient();
        var invalidDocument = JsonSerializer.Deserialize<JsonElement>("\"not-an-object\"");

        var response = await client.PutAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/popup-info",
            JsonContent.Create(invalidDocument, LayerAuthoringJsonContext.Default.JsonElement));

        response.Be400BadRequest();
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/drawing-info")]
    [Endpoint("GET /api/v1/admin/metadata/layers/{layerId}/drawing-info")]
    public async Task PutDrawingInfo_ThenGet_RoundTripsRenderer()
    {
        var client = _fixture.CreateAdminClient();
        var document = JsonSerializer.Deserialize<JsonElement>("""
        {
            "renderer": {
                "type": "simple",
                "symbol": {
                    "type": "esriSMS",
                    "style": "esriSMSCircle",
                    "color": [255, 0, 0, 255],
                    "size": 8
                }
            }
        }
        """);

        var putResponse = await client.PutAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/drawing-info",
            JsonContent.Create(document, LayerAuthoringJsonContext.Default.JsonElement));
        putResponse.Be200Ok();

        var getResponse = await client.GetAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/drawing-info");
        getResponse.Be200Ok();

        var get = await DeserializeDocumentAsync(getResponse);
        get!.Success.Should().BeTrue();
        get.Data.Should().NotBeNull();
        get.Data!.Document.HasValue.Should().BeTrue();
        get.Data.Document!.Value.GetProperty("renderer").GetProperty("type").GetString().Should().Be("simple");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/drawing-info")]
    [Endpoint("GET /api/v1/admin/metadata/layers/{layerId}/drawing-info")]
    public async Task PutDrawingInfo_WithNullBody_ClearsStoredDocument()
    {
        var client = _fixture.CreateAdminClient();
        var document = JsonSerializer.Deserialize<JsonElement>("""
        { "renderer": { "type": "simple" } }
        """);

        var seedResponse = await client.PutAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/drawing-info",
            JsonContent.Create(document, LayerAuthoringJsonContext.Default.JsonElement));
        seedResponse.Be200Ok();

        var nullBody = JsonSerializer.Deserialize<JsonElement>("null");
        var clearResponse = await client.PutAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/drawing-info",
            JsonContent.Create(nullBody, LayerAuthoringJsonContext.Default.JsonElement));
        clearResponse.Be200Ok();

        var getResponse = await client.GetAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/drawing-info");
        getResponse.Be200Ok();

        var get = await DeserializeDocumentAsync(getResponse);
        get!.Data.Should().NotBeNull();
        get.Data!.Document.HasValue.Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/relationships")]
    [Endpoint("GET /api/v1/admin/metadata/layers/{layerId}/relationships")]
    public async Task PutRelationships_ThenGet_RoundTripsRelationship()
    {
        var client = _fixture.CreateAdminClient();

        // Seeded test graph: layer 0 has field "objectid"; layer 1 has field "related_id".
        var request = new LayerRelationshipUpdateRequest
        {
            Relationships =
            [
                new LayerRelationshipUpdateItem
                {
                    Id = "rel-authoring-test",
                    Name = "Authored Relationship",
                    Description = "Created by integration test",
                    RelatedLayerId = 1,
                    Role = "origin",
                    Cardinality = "one-to-many",
                    OriginField = "objectid",
                    DestinationField = "related_id",
                    EsriRelationshipId = 7,
                },
            ],
        };

        var putResponse = await client.PutAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/relationships",
            JsonContent.Create(request, LayerAuthoringJsonContext.Default.LayerRelationshipUpdateRequest));
        putResponse.Be200Ok();

        var put = await DeserializeRelationshipsAsync(putResponse);
        put!.Success.Should().BeTrue();
        put.Data.Should().NotBeNull();
        put.Data!.Relationships.Should().ContainSingle();
        put.Data.Relationships[0].Id.Should().Be("rel-authoring-test");
        put.Data.Relationships[0].RelatedLayerId.Should().Be(1);

        var getResponse = await client.GetAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/relationships");
        getResponse.Be200Ok();

        var get = await DeserializeRelationshipsAsync(getResponse);
        get!.Data.Should().NotBeNull();
        get.Data!.Relationships.Should().Contain(rel =>
            rel.Id == "rel-authoring-test"
            && rel.OriginField == "objectid"
            && rel.DestinationField == "related_id");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/relationships")]
    public async Task PutRelationships_WithUnknownOriginField_ReturnsBadRequest()
    {
        var client = _fixture.CreateAdminClient();
        var request = new LayerRelationshipUpdateRequest
        {
            Relationships =
            [
                new LayerRelationshipUpdateItem
                {
                    Id = "rel-bad-origin",
                    Name = "Bad Origin",
                    RelatedLayerId = 1,
                    Cardinality = "one-to-many",
                    OriginField = "does_not_exist",
                    DestinationField = "related_id",
                },
            ],
        };

        var response = await client.PutAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/relationships",
            JsonContent.Create(request, LayerAuthoringJsonContext.Default.LayerRelationshipUpdateRequest));

        response.Be400BadRequest();
    }

    private static async Task<Honua.Infrastructure.Models.ApiResponse<LayerAuthoringDocumentResponse>?> DeserializeDocumentAsync(
        HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize(
            payload,
            LayerAuthoringJsonContext.Default.ApiResponseLayerAuthoringDocumentResponse);
    }

    private static async Task<Honua.Infrastructure.Models.ApiResponse<LayerRelationshipResponse>?> DeserializeRelationshipsAsync(
        HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize(
            payload,
            LayerAuthoringJsonContext.Default.ApiResponseLayerRelationshipResponse);
    }
}
