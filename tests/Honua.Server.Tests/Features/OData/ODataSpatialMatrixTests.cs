// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.OData;

/// <summary>
/// Comprehensive OData v4 spatial filter tests validating geo.intersects and geo.distance
/// functions across multiple geometry types with SRID transformations.
/// Implements parity with OGC API Features Python test matrices per issue #200.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.ODataV4)]
public sealed class ODataSpatialMatrixTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const int TestLayerId = 0;
    private const string PendingSpatialFilterSupport = "Pending OData spatial filter parity (#200).";

    // Test data object IDs from odata.yaml
    private const long SanFranciscoId = 1;      // -122.4194, 37.7749
    private const long LosAngelesId = 2;        // -118.2437, 34.0522
    private const long SacramentoId = 3;        // -121.4944, 38.5816
    private const long SanDiegoId = 4;          // -117.1611, 32.7157
    private const long SeattleId = 6;           // -122.3321, 47.6062
    private const long PhoenixId = 10;          // -112.074, 33.4484
    private const long VirtualCityId = 13;      // No geometry

    public async Task InitializeAsync()
    {
        _fixture.UseSeed(Path.Combine("tests", "seed", "odata.yaml"));
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    #region geo.intersects - Point Geometry Tests

    [IntegrationTest]
    [Operation(Operations.SpatialQuery)]
    [Endpoint("GET /odata/Features({layerId})?$filter=geo.intersects(Geometry, geography'POLYGON(...)')")]
    public async Task GeoIntersects_PolygonContainingSinglePoint_ReturnsSingleFeature()
    {
        // Polygon tightly around San Francisco
        var filter = "geo.intersects(Geometry, geography'POLYGON((-122.5 37.7, -122.3 37.7, -122.3 37.85, -122.5 37.85, -122.5 37.7))')";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        features.Should().ContainSingle();
        features[0].GetProperty("ObjectId").GetInt64().Should().Be(SanFranciscoId);
    }

    [IntegrationTest]
    [Operation(Operations.SpatialQuery)]
    [Endpoint("GET /odata/Features({layerId})?$filter=geo.intersects(Geometry, geography'POLYGON(...)')")]
    public async Task GeoIntersects_PolygonContainingMultiplePoints_ReturnsMultipleFeatures()
    {
        // Large polygon covering California
        var filter = "geo.intersects(Geometry, geography'POLYGON((-124 32, -114 32, -114 42, -124 42, -124 32))')";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        // Should include California cities: SF, LA, Sacramento, San Diego, San Jose (5)
        features.Should().HaveCountGreaterThanOrEqualTo(5);

        var objectIds = features.Select(f => f.GetProperty("ObjectId").GetInt64()).ToArray();
        objectIds.Should().Contain(SanFranciscoId);
        objectIds.Should().Contain(LosAngelesId);
        objectIds.Should().Contain(SacramentoId);
        objectIds.Should().Contain(SanDiegoId);
    }

    [IntegrationTest]
    [Operation(Operations.SpatialQuery)]
    [Endpoint("GET /odata/Features({layerId})?$filter=geo.intersects(Geometry, geography'POLYGON(...)')")]
    public async Task GeoIntersects_PolygonExcludingPoints_ReturnsNoFeatures()
    {
        // Polygon in the Atlantic Ocean - no features
        var filter = "geo.intersects(Geometry, geography'POLYGON((-80 25, -70 25, -70 35, -80 35, -80 25))')";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        features.Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.SpatialQuery)]
    [Endpoint("GET /odata/Features({layerId})?$filter=geo.intersects(Geometry, geography'POINT(...)')")]
    public async Task GeoIntersects_PointAtExactLocation_ReturnsSingleFeature()
    {
        // Exact location of San Francisco
        var filter = "geo.intersects(Geometry, geography'POINT(-122.4194 37.7749)')";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        features.Should().ContainSingle();
        features[0].GetProperty("ObjectId").GetInt64().Should().Be(SanFranciscoId);
    }

    #endregion

    #region geo.distance - Distance Filter Tests

    [IntegrationTest]
    [Operation(Operations.SpatialQuery)]
    [Endpoint("GET /odata/Features({layerId})?$filter=geo.distance(Geometry, geography'POINT(...)') lt ...")]
    public async Task GeoDistance_LessThan_ReturnsNearbyFeatures()
    {
        // Find cities within 100km (100000m) of San Francisco
        var filter = "geo.distance(Geometry, geography'POINT(-122.4194 37.7749)') lt 100000";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);

        // San Francisco should be included (distance = 0)
        var objectIds = features.Select(f => f.GetProperty("ObjectId").GetInt64()).ToArray();
        objectIds.Should().Contain(SanFranciscoId);

        // Distant cities should NOT be included
        objectIds.Should().NotContain(SeattleId); // ~1,100 km away
        objectIds.Should().NotContain(PhoenixId); // ~1,050 km away
    }

    [IntegrationTest]
    [Operation(Operations.SpatialQuery)]
    [Endpoint("GET /odata/Features({layerId})?$filter=geo.distance(Geometry, geography'POINT(...)') le ...")]
    public async Task GeoDistance_LessThanOrEqual_IncludesBoundary()
    {
        // Very small radius - only self should match
        var filter = "geo.distance(Geometry, geography'POINT(-122.4194 37.7749)') le 1";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);

        features.Should().ContainSingle();
        features[0].GetProperty("ObjectId").GetInt64().Should().Be(SanFranciscoId);
    }

    [IntegrationTest]
    [Operation(Operations.SpatialQuery)]
    [Endpoint("GET /odata/Features({layerId})?$filter=geo.distance(Geometry, geography'POINT(...)') gt ...")]
    public async Task GeoDistance_GreaterThan_ExcludesNearbyFeatures()
    {
        // Find cities MORE than 500km from San Francisco
        var filter = "geo.distance(Geometry, geography'POINT(-122.4194 37.7749)') gt 500000";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);

        var objectIds = features.Select(f => f.GetProperty("ObjectId").GetInt64()).ToArray();
        // San Francisco and nearby cities should NOT be included
        objectIds.Should().NotContain(SanFranciscoId);
        // Distant cities like Seattle, Phoenix should be included
        objectIds.Should().Contain(SeattleId);
        objectIds.Should().Contain(PhoenixId);
    }

    [IntegrationTest]
    [Operation(Operations.SpatialQuery)]
    [Endpoint("GET /odata/Features({layerId})?$filter=geo.distance(Geometry, geography'POINT(...)') ge ...")]
    public async Task GeoDistance_GreaterThanOrEqual_IncludesBoundary()
    {
        // Very large radius - should exclude close features
        var filter = "geo.distance(Geometry, geography'POINT(-122.4194 37.7749)') ge 1000000";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);

        // Seattle is ~1,100 km from SF
        var objectIds = features.Select(f => f.GetProperty("ObjectId").GetInt64()).ToArray();
        objectIds.Should().Contain(SeattleId);
    }

    #endregion

    #region Spatial Filters with SRID Transformations

    [IntegrationTest]
    [Operation(Operations.SpatialQuery)]
    [Endpoint("GET /odata/Features({layerId})?$filter=geo.intersects() with SRID")]
    public async Task GeoIntersects_WithExplicitSrid4326_TransformsCorrectly()
    {
        // Polygon with explicit SRID 4326
        var filter = "geo.intersects(Geometry, geography'SRID=4326;POLYGON((-123 37, -122 37, -122 38, -123 38, -123 37))')";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        features.Should().NotBeEmpty();
    }

    #endregion

    #region Spatial with Attribute Filters

    [IntegrationTest]
    [Operation(Operations.SpatialQuery)]
    [Endpoint("GET /odata/Features({layerId})?$filter=geo.intersects() and ...")]
    public async Task GeoIntersects_CombinedWithAttributeFilter_ReturnsCombinedResult()
    {
        // California polygon AND is_capital eq true
        var filter = "geo.intersects(Geometry, geography'POLYGON((-124 32, -114 32, -114 42, -124 42, -124 32))') and is_capital eq true";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);

        // Only Sacramento should match (California capital)
        features.Should().ContainSingle();
        features[0].GetProperty("ObjectId").GetInt64().Should().Be(SacramentoId);
    }

    [IntegrationTest]
    [Operation(Operations.SpatialQuery)]
    [Endpoint("GET /odata/Features({layerId})?$filter=geo.distance() and ...")]
    public async Task GeoDistance_CombinedWithPopulationFilter_ReturnsCombinedResult()
    {
        // Within 600km of LA AND population > 1M
        var filter = "geo.distance(Geometry, geography'POINT(-118.2437 34.0522)') lt 600000 and population gt 1000000";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);

        // LA, San Diego, San Jose, Phoenix are within 600km of LA with pop > 1M
        var objectIds = features.Select(f => f.GetProperty("ObjectId").GetInt64()).ToArray();
        objectIds.Should().Contain(LosAngelesId);
        objectIds.Should().Contain(SanDiegoId);
    }

    [IntegrationTest(Skip = PendingSpatialFilterSupport)]
    [Operation(Operations.SpatialQuery)]
    [Endpoint("GET /odata/Features({layerId})?$filter=geo.intersects() or geo.distance()")]
    public async Task Spatial_OrCombination_ReturnsUnion()
    {
        // In California OR within 50km of Seattle
        var filter = "geo.intersects(Geometry, geography'POLYGON((-124 32, -114 32, -114 42, -124 42, -124 32))') or geo.distance(Geometry, geography'POINT(-122.3321 47.6062)') lt 50000";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);

        var objectIds = features.Select(f => f.GetProperty("ObjectId").GetInt64()).ToArray();
        // Should include all California cities AND Seattle
        objectIds.Should().Contain(SanFranciscoId);
        objectIds.Should().Contain(SeattleId);
    }

    #endregion

    #region Spatial with Query Parameters

    [IntegrationTest]
    [Operation(Operations.SpatialQuery)]
    [Endpoint("GET /odata/Features({layerId})?$filter=geo.intersects()&$orderby=...")]
    public async Task GeoIntersects_WithOrderBy_ReturnsOrderedResults()
    {
        var filter = "geo.intersects(Geometry, geography'POLYGON((-124 32, -114 32, -114 42, -124 42, -124 32))')";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}&$orderby=population desc");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);

        // LA should be first (highest population in California)
        features.First().GetProperty("ObjectId").GetInt64().Should().Be(LosAngelesId);
    }

    [IntegrationTest]
    [Operation(Operations.SpatialQuery)]
    [Endpoint("GET /odata/Features({layerId})?$filter=geo.intersects()&$count=true")]
    public async Task GeoIntersects_WithCount_ReturnsTotalCount()
    {
        var filter = "geo.intersects(Geometry, geography'POLYGON((-124 32, -114 32, -114 42, -124 42, -124 32))')";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}&$count=true");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        document.RootElement.TryGetProperty("@odata.count", out var count).Should().BeTrue();
        count.GetInt64().Should().BeGreaterThanOrEqualTo(5); // At least 5 California cities
    }

    [IntegrationTest]
    [Operation(Operations.SpatialQuery)]
    [Endpoint("GET /odata/Features({layerId})?$filter=geo.distance()&$top=...&$skip=...")]
    public async Task GeoDistance_WithPagination_ReturnsPaginatedResults()
    {
        var filter = "geo.distance(Geometry, geography'POINT(-122.4194 37.7749)') lt 1000000";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}&$top=2&$skip=1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);
        features.Should().HaveCount(2);
    }

    #endregion

    #region Null Geometry Handling

    [IntegrationTest]
    [Operation(Operations.SpatialQuery)]
    [Endpoint("GET /odata/Features({layerId})?$filter=geo.intersects() with null geometry")]
    public async Task GeoIntersects_WithNullGeometry_ExcludesNullGeometries()
    {
        // Large polygon that would include Virtual City's location if it had geometry
        var filter = "geo.intersects(Geometry, geography'POLYGON((-180 -90, 180 -90, 180 90, -180 90, -180 -90))')";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);

        // Virtual City (ID 13) has null geometry and should NOT be included
        var objectIds = features.Select(f => f.GetProperty("ObjectId").GetInt64()).ToArray();
        objectIds.Should().NotContain(VirtualCityId);
    }

    [IntegrationTest(Skip = PendingSpatialFilterSupport)]
    [Operation(Operations.SpatialQuery)]
    [Endpoint("GET /odata/Features({layerId})?$filter=Geometry eq null")]
    public async Task Filter_GeometryEqualsNull_ReturnsNullGeometries()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter=Geometry eq null");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);

        // Only Virtual City has null geometry
        features.Should().ContainSingle();
        features[0].GetProperty("ObjectId").GetInt64().Should().Be(VirtualCityId);
    }

    [IntegrationTest(Skip = PendingSpatialFilterSupport)]
    [Operation(Operations.SpatialQuery)]
    [Endpoint("GET /odata/Features({layerId})?$filter=Geometry ne null")]
    public async Task Filter_GeometryNotEqualsNull_ExcludesNullGeometries()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$filter=Geometry ne null");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);

        // All except Virtual City (14 features)
        features.Should().HaveCount(14);
        var objectIds = features.Select(f => f.GetProperty("ObjectId").GetInt64()).ToArray();
        objectIds.Should().NotContain(VirtualCityId);
    }

    #endregion

    #region SRID Test Layer - Multi-Geometry Type Tests

    [IntegrationTest]
    [Operation(Operations.SpatialQuery)]
    [Endpoint("GET /odata/Features({layerId})?$filter=geo.intersects() SRID 3857 layer")]
    public async Task GeoIntersects_OnSrid3857Layer_TransformsFilterToLayerSrid()
    {
        // Use the spatial-reference seed with SRID 3857 layers
        var sridFixture = new WebAppFixture();
        sridFixture.UseSeed(Path.Combine("tests", "seed", "spatial-reference.yaml"));
        await sridFixture.InitializeAsync();

        try
        {
            // Insert a test point
            var schema = sridFixture.CurrentSchema ?? throw new InvalidOperationException("Schema was not initialized.");
            var objectId = await SpatialReferenceTestData.InsertPointAsync(
                sridFixture.Postgres,
                schema,
                SpatialReferenceTestLayerCatalog.PointLayerId,
                -122.4194,
                37.7749,
                "Test Point");

            // Query with WGS84 (4326) polygon - should transform to layer's 3857
            var filter = "geo.intersects(Geometry, geography'POLYGON((-122.5 37.7, -122.3 37.7, -122.3 37.9, -122.5 37.9, -122.5 37.7))')";
            var response = await sridFixture.Client.GetAsync(
                $"/odata/Features({SpatialReferenceTestLayerCatalog.PointLayerId})?$filter={Uri.EscapeDataString(filter)}");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var features = await ParseFeaturesAsync(response);
            features.Should().ContainSingle();
            features[0].GetProperty("ObjectId").GetInt64().Should().Be(objectId);
        }
        finally
        {
            await sridFixture.DisposeAsync();
        }
    }

    #endregion

    #region Helper Methods

    private static async Task<List<JsonElement>> ParseFeaturesAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        return document.RootElement.GetProperty("value")
            .EnumerateArray()
            .Select(e => e.Clone())
            .ToList();
    }

    #endregion
}
