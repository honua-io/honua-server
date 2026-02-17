// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.FeatureServer;

[Collection("Database")]
[Protocol(Protocols.FeatureServer)]
public sealed class FeatureServerSpatialLimitsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ConfigureWebHost(builder => builder.ConfigureAppConfiguration((_, configBuilder) =>
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Limits:Query:MaxBboxAreaSqKm"] = "10"
            })));

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_WithBoundingBoxExceedingConfiguredAreaLimit_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query" +
            "?geometry=-180,-90,180,90&geometryType=esriGeometryEnvelope&spatialRel=esriSpatialRelIntersects&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Geometry bounding box area");
        content.Should().Contain("exceeds maximum allowed area");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_WithBoundingBoxWithinConfiguredAreaLimit_ReturnsSuccess()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query" +
            "?geometry=-122.4200,37.7700,-122.4100,37.7800&geometryType=esriGeometryEnvelope&spatialRel=esriSpatialRelIntersects&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
