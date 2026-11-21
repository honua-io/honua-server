// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

[Protocol(TestProtocols.FeatureServer)]
[Collection("Database")]
public sealed class FeatureServerQueryConformanceTests(WebAppFixture fixture) : IClassFixture<WebAppFixture>
{
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_EmptyReturnExtentOnlyWithPbf_ReturnsProtobufContentType()
    {
        using var response = await fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=pbf&where=1%3D0&returnExtentOnly=true");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/x-protobuf");
        var payload = await response.Content.ReadAsByteArrayAsync();
        payload.Should().NotBeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/query")]
    public async Task ServiceQuery_ReturnExtentOnlyAndReturnCountOnly_ReturnsBothExtentAndCount()
    {
        var layerDefs = Uri.EscapeDataString("""[{"layerId":0,"where":"1=1"}]""");
        using var response = await fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/query?f=json&layerDefs={layerDefs}&returnExtentOnly=true&returnCountOnly=true");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        using var doc = JsonDocument.Parse(content);
        var layerResult = doc.RootElement.GetProperty("layers")[0];
        layerResult.GetProperty("count").GetInt64().Should().Be(5);
        layerResult.TryGetProperty("extent", out var extent).Should().BeTrue(content);
        extent.ValueKind.Should().Be(JsonValueKind.Object);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/query")]
    public async Task ServiceQuery_ReturnIdsOnly_ReturnsObjectIdsWithoutExceededTransferLimit()
    {
        var layerDefs = Uri.EscapeDataString("""[{"layerId":0,"where":"1=1"}]""");
        using var response = await fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/query?f=json&layerDefs={layerDefs}&returnIdsOnly=true");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        using var doc = JsonDocument.Parse(content);
        var layerResult = doc.RootElement.GetProperty("layers")[0];
        layerResult.GetProperty("objectIds").GetArrayLength().Should().Be(5);
        var exceeded = layerResult.TryGetProperty("exceededTransferLimit", out var prop) && prop.GetBoolean();
        exceeded.Should().BeFalse(content);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_PbfWithWebMercatorAlias_ReturnsProtobuf()
    {
        using var response = await fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=pbf&where=1%3D1&outSR=102100");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/x-protobuf");
        var payload = await response.Content.ReadAsByteArrayAsync();
        payload.Should().NotBeEmpty();
    }
}
