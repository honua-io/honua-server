// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Integration tests asserting that <c>returnDistance</c> is spec-compliant for the
/// general layer <c>/query</c> operation, not just K-nearest-neighbor queries: when a
/// spatial filter geometry is supplied, each matched feature carries its geodesic
/// distance (meters) from the query geometry under the runtime <c>distance</c> attribute.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerReturnDistanceQueryTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const string TestServiceId = "test";
    private const int TestLayerId = 0;

    // San Francisco; the seed dataset places features within a 50km radius.
    private const string PointGeometry = @"{""x"":-122.4194,""y"":37.7749}";

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    private static QueryResponse Deserialize(string content)
    {
        var response = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);
        response.Should().NotBeNull();
        return response!;
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_WithinDistanceAndReturnDistance_IncludesDistanceAttribute()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
            $"?geometry={Uri.EscapeDataString(PointGeometry)}" +
            $"&spatialRel=esriSpatialRelWithinDistance" +
            $"&distance=50000&units=esriSRUnit_Meter" +
            $"&returnDistance=true&f=json");

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = Deserialize(content);

        queryResponse.Features.Should().NotBeNullOrEmpty();
        queryResponse.Fields.Should().Contain(field => field.Name.Equals("distance", StringComparison.OrdinalIgnoreCase));
        queryResponse.Features![0].Attributes.Should().ContainKey("distance");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_IntersectsWithoutReturnDistance_OmitsDistanceAttribute()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
            $"?geometry={Uri.EscapeDataString(PointGeometry)}" +
            $"&spatialRel=esriSpatialRelWithinDistance" +
            $"&distance=50000&units=esriSRUnit_Meter" +
            $"&f=json");

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = Deserialize(content);

        queryResponse.Fields.Should().NotContain(field => field.Name.Equals("distance", StringComparison.OrdinalIgnoreCase));
        if (queryResponse.Features is { Length: > 0 } features)
        {
            features[0].Attributes.Should().NotContainKey("distance");
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_ReturnDistanceWithoutGeometry_IsIgnored()
    {
        // returnDistance has no effect without a spatial filter; the query still succeeds
        // and simply omits the distance attribute.
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
            $"?where=1=1&returnDistance=true&f=json");

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = Deserialize(content);

        queryResponse.Fields.Should().NotContain(field => field.Name.Equals("distance", StringComparison.OrdinalIgnoreCase));
    }
}
