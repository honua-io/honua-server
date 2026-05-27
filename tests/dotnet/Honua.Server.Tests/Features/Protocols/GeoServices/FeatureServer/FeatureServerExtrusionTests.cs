// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Integration tests for v1 extruded 3D feature layer metadata output
/// surfaced through the GeoServices FeatureServer layer metadata response.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerExtrusionTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task LayerMetadata_NoExtrusionConfigured_OmitsExtrusionInfoFromResponse()
    {
        var client = _fixture.CreateClient();

        var response = await client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(payload);

        document.RootElement.TryGetProperty("extrusionInfo", out _).Should().BeFalse(
            "2D-only layers must not surface extrusionInfo in the FeatureServer response");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task LayerMetadata_WithExtrusionConfigured_SurfacesExtrusionInfo()
    {
        await SetLayerExtrusionMetadataAsync(new LayerExtrusionInfo
        {
            HeightField = "objectid",
            Unit = VerticalUnits.Meters,
            DefaultHeight = 3.0,
            MaterialHint = "concrete"
        });

        var client = _fixture.CreateClient();

        var response = await client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(payload);

        document.RootElement.TryGetProperty("extrusionInfo", out var extrusionInfo).Should().BeTrue();
        extrusionInfo.GetProperty("enabled").GetBoolean().Should().BeTrue();
        extrusionInfo.GetProperty("heightField").GetString().Should().Be("objectid");
        extrusionInfo.GetProperty("unit").GetString().Should().Be("meters");
        extrusionInfo.GetProperty("defaultHeight").GetDouble().Should().Be(3.0);
        extrusionInfo.GetProperty("materialHint").GetString().Should().Be("concrete");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task LayerMetadata_WithBaseHeightField_SurfacesBaseHeightField()
    {
        await SetLayerExtrusionMetadataAsync(new LayerExtrusionInfo
        {
            HeightField = "objectid",
            BaseHeightField = "objectid",
            Unit = VerticalUnits.Feet
        });

        var client = _fixture.CreateClient();

        var response = await client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(payload);

        var extrusionInfo = document.RootElement.GetProperty("extrusionInfo");
        extrusionInfo.GetProperty("baseHeightField").GetString().Should().Be("objectid");
        extrusionInfo.GetProperty("unit").GetString().Should().Be("feet");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task LayerMetadata_HeightFieldMissing_Returns422WithErrorCode()
    {
        await SetRawMetadataJsonAsync("""{"extrusion":{"unit":"meters"}}""");

        var client = _fixture.CreateClient();

        var response = await client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().Contain(ExtrusionErrorCodes.HeightFieldMissing);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task LayerMetadata_HeightFieldNotFound_Returns422WithErrorCode()
    {
        await SetLayerExtrusionMetadataAsync(new LayerExtrusionInfo
        {
            HeightField = "ghost_field"
        });

        var client = _fixture.CreateClient();

        var response = await client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().Contain(ExtrusionErrorCodes.HeightFieldNotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task LayerMetadata_HeightFieldNonNumeric_Returns422WithErrorCode()
    {
        await SetLayerExtrusionMetadataAsync(new LayerExtrusionInfo
        {
            HeightField = "name"
        });

        var client = _fixture.CreateClient();

        var response = await client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().Contain(ExtrusionErrorCodes.HeightFieldTypeInvalid);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task LayerMetadata_UnknownUnit_Returns422WithErrorCode()
    {
        // Unknown unit tokens in raw catalog metadata must surface through
        // the validator as EXTRUSION_UNIT_UNRECOGNIZED rather than throw
        // during catalog deserialization.
        await SetRawMetadataJsonAsync("""{"extrusion":{"heightField":"objectid","unit":"yards"}}""");

        var client = _fixture.CreateClient();

        var response = await client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().Contain(ExtrusionErrorCodes.UnitUnrecognized);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task LayerMetadata_NegativeDefaultHeight_Returns422WithErrorCode()
    {
        await SetLayerExtrusionMetadataAsync(new LayerExtrusionInfo
        {
            HeightField = "objectid",
            DefaultHeight = -1.0
        });

        var client = _fixture.CreateClient();

        var response = await client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().Contain(ExtrusionErrorCodes.NegativeDefaultHeight);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task LayerQuery_WithExtrusionConfigured_DoesNotAffect2DResponse()
    {
        await SetLayerExtrusionMetadataAsync(new LayerExtrusionInfo
        {
            HeightField = "objectid",
            Unit = VerticalUnits.Meters
        });

        var client = _fixture.CreateClient();

        var response = await client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?where=1%3D1&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(payload);

        document.RootElement.TryGetProperty("features", out _).Should().BeTrue(
            "2D feature query must remain unaffected when extrusion is configured");
        document.RootElement.TryGetProperty("extrusionInfo", out _).Should().BeFalse(
            "extrusionInfo must not leak into query payloads");
    }

    private async Task SetLayerExtrusionMetadataAsync(LayerExtrusionInfo extrusion)
    {
        var metadata = new CatalogMetadata { Extrusion = extrusion };
        var json = JsonSerializer.Serialize(metadata, CatalogJsonContext.Default.CatalogMetadata);
        await SetRawMetadataJsonAsync(json);
    }

    private Task SetRawMetadataJsonAsync(string metadataJson)
    {
        var metadata = JsonSerializer.Deserialize(metadataJson, CatalogJsonContext.Default.CatalogMetadata)
            ?? new CatalogMetadata();
        _fixture.UpdateV2ResourceMetadata(
            WebAppFixture.TestLayerId,
            extrusion: metadata.Extrusion,
            clearExtrusion: metadata.Extrusion is null);
        return Task.CompletedTask;
    }
}
