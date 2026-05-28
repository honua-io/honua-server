// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit.Helpers;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.SpatialAnalytics;

/// <summary>
/// OGC API Features mirror tests for the Pro-tier spatial analytics endpoints.
/// These act as a smoke-test suite verifying that each OGC route is wired to
/// the same shared core handlers exercised by <see cref="SpatialAnalyticsRestTests"/>.
/// The REST suite covers comprehensive parameter validation; this suite checks
/// that the <c>/ogc/features/collections/{collectionId}/...</c> surface returns
/// the same GeoJSON shape for a happy-path request and produces consistent
/// error responses when the collection does not exist or a required parameter
/// is missing.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.SpatialAnalytics)]
public sealed class SpatialAnalyticsOgcTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .WithTestLicense(HonuaEdition.Pro);

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    private static StringContent JsonBody(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private static async Task AssertSpatialAnalyticsFeatureCollectionAsync(
        HttpResponseMessage response, string expectedOperation)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        root.GetProperty("type").GetString().Should().Be("FeatureCollection");
        root.GetProperty("features").ValueKind.Should().Be(JsonValueKind.Array);
        root.GetProperty("metadata").GetProperty("operation").GetString().Should().Be(expectedOperation);
    }

    // ---------- Clusters mirror ----------

    [IntegrationTest]
    [Operation(Operations.QueryClusters)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/clusters")]
    public async Task OgcClusters_Dbscan_ReturnsFeatureCollection()
    {
        var response = await _fixture.Client.PostAsync(
            $"/ogc/features/collections/{WebAppFixture.TestLayerId}/clusters",
            JsonBody(new { algorithm = "dbscan", eps = 50000, minPoints = 1 }));

        await AssertSpatialAnalyticsFeatureCollectionAsync(response, "cluster");
    }

    [IntegrationTest]
    [Operation(Operations.QueryClusters)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/clusters")]
    public async Task OgcClusters_OmitAlgorithm_DefaultsToDbscan()
    {
        // The OGC clusters contract documents `algorithm` as optional with a
        // default of "dbscan"; omitting it must resolve to DBSCAN rather than
        // returning a 400. The DBSCAN-specific parameters (eps, minPoints) are
        // still required and supplied here.
        var response = await _fixture.Client.PostAsync(
            $"/ogc/features/collections/{WebAppFixture.TestLayerId}/clusters",
            JsonBody(new { eps = 50000, minPoints = 1 }));

        await AssertSpatialAnalyticsFeatureCollectionAsync(response, "cluster");
    }

    [IntegrationTest]
    [Operation(Operations.QueryClusters)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/clusters")]
    public async Task OgcClusters_MissingEps_ReturnsBadRequest()
    {
        var response = await _fixture.Client.PostAsync(
            $"/ogc/features/collections/{WebAppFixture.TestLayerId}/clusters",
            JsonBody(new { algorithm = "dbscan", minPoints = 1 }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.QueryClusters)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/clusters")]
    public async Task OgcClusters_InvalidCollection_ReturnsNotFound()
    {
        var response = await _fixture.Client.PostAsync(
            "/ogc/features/collections/99999/clusters",
            JsonBody(new { algorithm = "dbscan", eps = 50000, minPoints = 1 }));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---------- Spatial join mirror ----------

    [IntegrationTest]
    [Operation(Operations.SpatialJoin)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/spatial-join")]
    public async Task OgcSpatialJoin_Intersects_ReturnsFeatureCollection()
    {
        var response = await _fixture.Client.PostAsync(
            $"/ogc/features/collections/{WebAppFixture.TestLayerId}/spatial-join",
            JsonBody(new { joinLayerId = 1, predicate = "intersects" }));

        await AssertSpatialAnalyticsFeatureCollectionAsync(response, "spatial-join");
    }

    [IntegrationTest]
    [Operation(Operations.SpatialJoin)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/spatial-join")]
    public async Task OgcSpatialJoin_MissingJoinLayer_ReturnsBadRequest()
    {
        var response = await _fixture.Client.PostAsync(
            $"/ogc/features/collections/{WebAppFixture.TestLayerId}/spatial-join",
            JsonBody(new { predicate = "intersects" }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.SpatialJoin)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/spatial-join")]
    public async Task OgcSpatialJoin_InvalidCollection_ReturnsNotFound()
    {
        var response = await _fixture.Client.PostAsync(
            "/ogc/features/collections/99999/spatial-join",
            JsonBody(new { joinLayerId = 1, predicate = "intersects" }));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---------- Buffer aggregate mirror ----------

    [IntegrationTest]
    [Operation(Operations.QueryBufferAggregate)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/buffer-aggregate")]
    public async Task OgcBufferAggregate_Dissolve_ReturnsFeatureCollection()
    {
        var response = await _fixture.Client.PostAsync(
            $"/ogc/features/collections/{WebAppFixture.TestLayerId}/buffer-aggregate",
            JsonBody(new { distance = 500, unit = "meters", dissolve = true }));

        await AssertSpatialAnalyticsFeatureCollectionAsync(response, "buffer-aggregate");
    }

    [IntegrationTest]
    [Operation(Operations.QueryBufferAggregate)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/buffer-aggregate")]
    public async Task OgcBufferAggregate_MissingDistance_ReturnsBadRequest()
    {
        var response = await _fixture.Client.PostAsync(
            $"/ogc/features/collections/{WebAppFixture.TestLayerId}/buffer-aggregate",
            JsonBody(new { unit = "meters", dissolve = true }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.QueryBufferAggregate)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/buffer-aggregate")]
    public async Task OgcBufferAggregate_InvalidCollection_ReturnsNotFound()
    {
        var response = await _fixture.Client.PostAsync(
            "/ogc/features/collections/99999/buffer-aggregate",
            JsonBody(new { distance = 500, unit = "meters", dissolve = true }));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---------- Density mirror ----------

    [IntegrationTest]
    [Operation(Operations.QueryDensity)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/density")]
    public async Task OgcDensity_HexGrid_ReturnsFeatureCollection()
    {
        var response = await _fixture.Client.PostAsync(
            $"/ogc/features/collections/{WebAppFixture.TestLayerId}/density",
            JsonBody(new { mode = "hexGrid", cellSize = 20000 }));

        await AssertSpatialAnalyticsFeatureCollectionAsync(response, "density");
    }

    [IntegrationTest]
    [Operation(Operations.QueryDensity)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/density")]
    public async Task OgcDensity_MissingCellSize_ReturnsBadRequest()
    {
        var response = await _fixture.Client.PostAsync(
            $"/ogc/features/collections/{WebAppFixture.TestLayerId}/density",
            JsonBody(new { mode = "hexGrid" }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.QueryDensity)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/density")]
    public async Task OgcDensity_InvalidCollection_ReturnsNotFound()
    {
        var response = await _fixture.Client.PostAsync(
            "/ogc/features/collections/99999/density",
            JsonBody(new { mode = "hexGrid", cellSize = 20000 }));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
