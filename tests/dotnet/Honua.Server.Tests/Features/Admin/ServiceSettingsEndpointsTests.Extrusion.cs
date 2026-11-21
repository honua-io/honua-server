// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.Admin;

// The layer metadata PUT stores a valid extrusion block and refuses an invalid one with 422.
public sealed partial class ServiceSettingsEndpointsTests
{
    private const string LayerMetadataPath = "/api/v1/admin/services/test/layers/0/metadata";

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/layers/{serviceLayerIndex}/metadata")]
    [Endpoint("GET /rest/services/{serviceName}/FeatureServer/{layerId}")]
    public async Task UpdateLayerMetadata_WithValidExtrusion_StoresAndPublishesIt()
    {
        var heightField = await FindFieldAsync(IsNumericField);

        var response = await PutExtrusionAsync($$"""{"heightField":"{{heightField}}","unit":"Feet","defaultHeight":3}""");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var admin = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var extrusion = admin.RootElement.GetProperty("data").GetProperty("extrusion");
        extrusion.GetProperty("heightField").GetString().Should().Be(heightField);
        extrusion.GetProperty("unit").GetString().Should().Be("feet");

        using var layer = await GetFeatureServerLayerAsync();
        layer.RootElement.GetProperty("extrusionInfo").GetProperty("heightField").GetString().Should().Be(heightField);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/layers/{serviceLayerIndex}/metadata")]
    public async Task UpdateLayerMetadata_WithUnknownHeightField_Returns422WithoutStoring()
    {
        var response = await PutExtrusionAsync("""{"heightField":"no_such_height_field"}""");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("code").GetString().Should().Be("EXTRUSION_HEIGHT_FIELD_NOT_FOUND");

        using var layer = await GetFeatureServerLayerAsync();
        var stored = layer.RootElement.TryGetProperty("extrusionInfo", out var info) && info.ValueKind != JsonValueKind.Null;
        stored.Should().BeFalse();
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/layers/{serviceLayerIndex}/metadata")]
    public async Task UpdateLayerMetadata_WithTextHeightField_Returns422()
    {
        var textField = await FindFieldAsync(field => field.GetProperty("type").GetString() == "esriFieldTypeString");

        var response = await PutExtrusionAsync($$"""{"heightField":"{{textField}}"}""");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("code").GetString().Should().Be("EXTRUSION_HEIGHT_FIELD_TYPE_INVALID");
    }

    private async Task<HttpResponseMessage> PutExtrusionAsync(string extrusionJson)
    {
        using var content = new StringContent($$"""{"extrusion":{{extrusionJson}}}""", Encoding.UTF8, "application/json");
        return await _client.PutAsync(LayerMetadataPath, content);
    }

    private async Task<JsonDocument> GetFeatureServerLayerAsync()
    {
        var response = await _client.GetAsync("/rest/services/test/FeatureServer/0?f=json");
        response.Be200Ok();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private async Task<string> FindFieldAsync(Func<JsonElement, bool> predicate)
    {
        using var layer = await GetFeatureServerLayerAsync();
        return layer.RootElement.GetProperty("fields").EnumerateArray()
            .First(predicate)
            .GetProperty("name").GetString()!;
    }

    private static bool IsNumericField(JsonElement field)
        => field.GetProperty("type").GetString() is "esriFieldTypeInteger" or "esriFieldTypeSmallInteger"
            or "esriFieldTypeDouble" or "esriFieldTypeSingle" or "esriFieldTypeBigInteger";
}
