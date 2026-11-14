// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

public sealed partial class FeatureServerSpatialReferenceTests
{
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_ExplicitRealizationOnProjectedLayer_MatchesIndependentReference()
    {
        var requestUri = $"/rest/services/{SpatialReferenceTestLayerCatalog.ServiceId}/FeatureServer/{SpatialReferenceTestLayerCatalog.PointLayerId}/query" +
                         $"?where=objectid%20%3D%20{_pointObjectId}&outSR=4269&datumTransformation=108190&f=json";
        using var response = await _fixture.Client.GetAsync(requestUri);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        var geometry = document.RootElement.GetProperty("features")[0].GetProperty("geometry");
        geometry.GetProperty("x").GetDouble().Should().BeApproximately(-122.4193867848092, 8e-8);
        geometry.GetProperty("y").GetDouble().Should().BeApproximately(37.77489505579728, 8e-8);
    }
    [IntegrationTheory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("2020,5")]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_InvalidCoordinateEpoch_ReturnsClientError(string epoch)
    {
        var requestUri = $"/rest/services/{SpatialReferenceTestLayerCatalog.ServiceId}/FeatureServer/{SpatialReferenceTestLayerCatalog.PointLayerId}/query" +
                         $"?where=objectid%20%3D%20{_pointObjectId}&outSR=4269&datumTransformation=108190&coordinateEpoch={Uri.EscapeDataString(epoch)}&f=json";
        using var response = await _fixture.Client.GetAsync(requestUri);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest, body);
        body.Should().Contain("coordinateEpoch");
    }
}
