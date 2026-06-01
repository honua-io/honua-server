// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Endpoint tests for three FeatureServer query capabilities flagged by the Esri SDK
/// certification matrix: <c>returnExtentOnly</c>, point + <c>distance</c>/<c>units</c>
/// spatial buffering, and the <c>sqlFormat</c> parameter.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerQueryGapsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    // Gap 1: returnExtentOnly must return the bounding envelope of matching features
    // plus a count, rather than 400-ing.
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithReturnExtentOnly_ReturnsExtentAndCount()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=json&where=1%3D1&returnExtentOnly=true");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        var queryResponse = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.QueryResponse);
        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().BeNull();

        queryResponse.Extent.Should().NotBeNull("returnExtentOnly must return an envelope");
        var extent = queryResponse.Extent!;
        extent.Xmax.Should().BeGreaterThan(extent.Xmin);
        extent.Ymax.Should().BeGreaterThan(extent.Ymin);
        extent.SpatialReference.Should().NotBeNull();

        // The seeded test layer has five features matching where=1=1 (count reflects all
        // matching rows); four carry geometry and define the returned envelope.
        queryResponse.Count.Should().Be(5);

        // Seeded points span roughly x:[-122.7,-121.9], y:[37.3,37.8].
        extent.Xmin.Should().BeApproximately(-122.7, 0.01);
        extent.Xmax.Should().BeApproximately(-121.9, 0.01);
        extent.Ymin.Should().BeApproximately(37.3, 0.01);
        extent.Ymax.Should().BeApproximately(37.8, 0.01);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithReturnExtentOnlyAndWhereFilter_RespectsFilter()
    {
        // category='sample' selects objectid 2 (-122.7,37.7) and 4 (-121.9,37.3).
        var where = Uri.EscapeDataString("category = 'sample'");
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=json&where={where}&returnExtentOnly=true");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        var queryResponse = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.QueryResponse);
        queryResponse.Should().NotBeNull();
        queryResponse!.Count.Should().Be(2);
        queryResponse.Extent.Should().NotBeNull();
        queryResponse.Extent!.Xmin.Should().BeApproximately(-122.7, 0.01);
        queryResponse.Extent.Xmax.Should().BeApproximately(-121.9, 0.01);
    }

    [IntegrationTest]
    [Operation(Operations.GetLayerInfo)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}")]
    public async Task LayerMetadata_AdvertisesSupportsReturningQueryExtent()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        document.RootElement.TryGetProperty("advancedQueryCapabilities", out var capabilities)
            .Should().BeTrue("layer metadata should expose advancedQueryCapabilities");
        capabilities.TryGetProperty("supportsReturningQueryExtent", out var supportsExtent)
            .Should().BeTrue("advancedQueryCapabilities should expose supportsReturningQueryExtent");
        supportsExtent.GetBoolean().Should().BeTrue();

        // The ArcGIS Maps SDK for JavaScript reads supportsReturningQueryExtent from the
        // layer object ROOT (p(layer, "supportsReturningQueryExtent", false)) when deciding
        // whether FeatureLayer.queryExtent() is permitted. It must be present at the top level
        // in addition to advancedQueryCapabilities, and stay consistent with it.
        document.RootElement.TryGetProperty("supportsReturningQueryExtent", out var rootSupportsExtent)
            .Should().BeTrue("layer root must expose supportsReturningQueryExtent for the JS SDK queryExtent() guard");
        rootSupportsExtent.GetBoolean().Should().Be(supportsExtent.GetBoolean(),
            "root supportsReturningQueryExtent must match advancedQueryCapabilities.supportsReturningQueryExtent");
    }

    // Gap 2: a point geometry plus distance + units must buffer the input before the
    // spatial test, matching features within the buffer. The probe point intersects no
    // feature exactly, so a missing buffer would return zero features.
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithPointAndDistanceBuffer_MatchesNearbyFeatures()
    {
        // Probe point ~6.9 km from feature 1 (-122.5,37.5). A 10 km buffer must capture it.
        var geometry = Uri.EscapeDataString("{\"x\":-122.45,\"y\":37.45,\"spatialReference\":{\"wkid\":4326}}");

        var bufferedResponse = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query" +
            $"?f=json&geometry={geometry}&geometryType=esriGeometryPoint&inSR=4326" +
            "&distance=10&units=esriSRUnit_Kilometer&returnGeometry=false&outFields=objectid");

        var bufferedContent = await bufferedResponse.Content.ReadAsStringAsync();
        bufferedResponse.StatusCode.Should().Be(HttpStatusCode.OK, bufferedContent);

        var buffered = JsonSerializer.Deserialize(bufferedContent, FeatureServerJsonContext.Default.QueryResponse);
        buffered.Should().NotBeNull();
        buffered!.Features.Should().NotBeNull();
        buffered.Features!.Should().NotBeEmpty("the 10 km buffer must capture feature 1");

        var matchedIds = buffered.Features!
            .Select(feature => GetObjectId(feature.Attributes))
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToList();
        matchedIds.Should().Contain(1, "feature 1 lies within the 10 km buffer");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithPointAndZeroDistance_DoesNotMatchOffsetFeatures()
    {
        // No distance => exact intersects against a point feature; the offset probe matches nothing.
        var geometry = Uri.EscapeDataString("{\"x\":-122.45,\"y\":37.45,\"spatialReference\":{\"wkid\":4326}}");

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query" +
            $"?f=json&geometry={geometry}&geometryType=esriGeometryPoint&inSR=4326&returnCountOnly=true");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        var queryResponse = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.QueryResponse);
        queryResponse.Should().NotBeNull();
        queryResponse!.Count.Should().Be(0, "without a distance buffer the offset probe intersects no feature");
    }

    // Gap 3: sqlFormat=standard and sqlFormat=native must both be accepted (200), not rejected.
    [Theory]
    [InlineData("standard")]
    [InlineData("native")]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithSqlFormat_ReturnsOk(string sqlFormat)
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=json&where=1%3D1&sqlFormat={sqlFormat}");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        var queryResponse = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.QueryResponse);
        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithInvalidSqlFormat_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=json&sqlFormat=bogus");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("sqlFormat");
    }

    private static long? GetObjectId(Dictionary<string, object?> attributes)
    {
        if (!attributes.TryGetValue("objectid", out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            long l => l,
            int i => i,
            JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var parsed) => parsed,
            _ => long.TryParse(value.ToString(), out var fallback) ? fallback : null
        };
    }
}
