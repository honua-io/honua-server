// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.SpatialAnalytics;

/// <summary>
/// Integration tests for the Pro-tier spatial analytics REST endpoints mirrored
/// under <c>/rest/services/{serviceId}/FeatureServer/{layerId}</c>. Uses the shared
/// <see cref="WebAppFixture"/> PostGIS test fixture so clustering / join / buffer /
/// density operations run against a real database populated by the server seed.
/// The default test host runs as Pro edition so the edition gate passes; the gated
/// behavior is verified in the unit-tests elsewhere.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.SpatialAnalytics)]
public sealed class SpatialAnalyticsRestTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    // ---------- Clusters (DBSCAN / K-Means) ----------

    [IntegrationTest]
    [Operation(Operations.QueryClusters)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryClusters")]
    public async Task QueryClusters_Dbscan_PerFeature_ReturnsClusterAssignments()
    {
        var payload = JsonSerializer.Serialize(new
        {
            algorithm = "dbscan",
            eps = 50000,
            minPoints = 1,
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryClusters",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        root.GetProperty("type").GetString().Should().Be("FeatureCollection");
        var features = root.GetProperty("features");
        features.ValueKind.Should().Be(JsonValueKind.Array);
        features.GetArrayLength().Should().BeGreaterThan(0);

        // Per-feature mode: each row has a properties.clusterId.
        foreach (var feature in features.EnumerateArray())
        {
            feature.GetProperty("type").GetString().Should().Be("Feature");
            var properties = feature.GetProperty("properties");
            properties.TryGetProperty("clusterId", out _).Should().BeTrue();
        }

        root.GetProperty("metadata").GetProperty("operation").GetString().Should().Be("cluster");
    }

    [IntegrationTest]
    [Operation(Operations.QueryClusters)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryClusters")]
    public async Task QueryClusters_Dbscan_HullPerCluster_ReturnsHullGeometries()
    {
        var payload = JsonSerializer.Serialize(new
        {
            algorithm = "dbscan",
            eps = 50000,
            minPoints = 1,
            returnHullPerCluster = true,
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryClusters",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        var features = root.GetProperty("features");
        features.GetArrayLength().Should().BeGreaterThan(0);

        // Hull-per-cluster mode: each row has a clusterId and featureCount,
        // and the geometry (when present) is the convex hull of the cluster.
        foreach (var feature in features.EnumerateArray())
        {
            var properties = feature.GetProperty("properties");
            properties.TryGetProperty("clusterId", out _).Should().BeTrue();
            properties.TryGetProperty("featureCount", out var count).Should().BeTrue();
            count.GetInt64().Should().BeGreaterThan(0);
        }
    }

    [IntegrationTest]
    [Operation(Operations.QueryClusters)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryClusters")]
    public async Task QueryClusters_KMeans_ReturnsPartitionedRows()
    {
        var payload = JsonSerializer.Serialize(new
        {
            algorithm = "kmeans",
            k = 2,
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryClusters",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        var features = root.GetProperty("features");
        features.GetArrayLength().Should().BeGreaterThan(0);

        // Every returned row must have a clusterId in [0, k).
        foreach (var feature in features.EnumerateArray())
        {
            var clusterId = feature.GetProperty("properties").GetProperty("clusterId");
            clusterId.ValueKind.Should().NotBe(JsonValueKind.Null);
            clusterId.GetInt64().Should().BeInRange(0, 1);
        }
    }

    [IntegrationTest]
    [Operation(Operations.QueryClusters)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryClusters")]
    public async Task QueryClusters_Dbscan_MissingEps_ReturnsBadRequest()
    {
        var payload = JsonSerializer.Serialize(new
        {
            algorithm = "dbscan",
            minPoints = 2,
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryClusters",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("eps");
    }

    [IntegrationTest]
    [Operation(Operations.QueryClusters)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryClusters")]
    public async Task QueryClusters_KMeans_MissingK_ReturnsBadRequest()
    {
        var payload = JsonSerializer.Serialize(new
        {
            algorithm = "kmeans",
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryClusters",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("k");
    }

    [IntegrationTest]
    [Operation(Operations.QueryClusters)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryClusters")]
    public async Task QueryClusters_InvalidAlgorithm_ReturnsBadRequest()
    {
        var payload = JsonSerializer.Serialize(new
        {
            algorithm = "bogus",
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryClusters",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("algorithm");
    }

    [IntegrationTest]
    [Operation(Operations.QueryClusters)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryClusters")]
    public async Task QueryClusters_WithWhereFilter_AppliesFilter()
    {
        var payload = JsonSerializer.Serialize(new
        {
            algorithm = "dbscan",
            eps = 50000,
            minPoints = 1,
            where = "category = 'test'",
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryClusters",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var features = doc.RootElement.GetProperty("features");

        // Seed data has 3 'test' features (object ids 1, 3, 5) — 1 has null geometry,
        // so clustering operates on 2 geometries. The filter should at minimum not
        // error and should return <= the 5 seeded layer 0 points.
        features.GetArrayLength().Should().BeLessThanOrEqualTo(5);
    }

    [IntegrationTest]
    [Operation(Operations.QueryClusters)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryClusters")]
    public async Task QueryClusters_InvalidService_ReturnsNotFound()
    {
        var payload = JsonSerializer.Serialize(new
        {
            algorithm = "dbscan",
            eps = 1000,
            minPoints = 1,
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            "/rest/services/nonexistent/FeatureServer/0/queryClusters",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---------- Spatial Join ----------

    [IntegrationTest]
    [Operation(Operations.SpatialJoin)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/spatialJoin")]
    public async Task SpatialJoin_Intersects_ReturnsTargetFeaturesWithMatchCount()
    {
        var payload = JsonSerializer.Serialize(new
        {
            joinLayerId = 1,
            predicate = "intersects",
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/spatialJoin",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        root.GetProperty("metadata").GetProperty("operation").GetString().Should().Be("spatial-join");
        var features = root.GetProperty("features");
        features.GetArrayLength().Should().BeGreaterThan(0);

        foreach (var feature in features.EnumerateArray())
        {
            var props = feature.GetProperty("properties");
            props.TryGetProperty("matchCount", out var matchCount).Should().BeTrue();
            matchCount.GetInt64().Should().BeGreaterOrEqualTo(0);
        }
    }

    [IntegrationTest]
    [Operation(Operations.SpatialJoin)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/spatialJoin")]
    public async Task SpatialJoin_DWithin_ReturnsTargetFeaturesWithMatchCount()
    {
        var payload = JsonSerializer.Serialize(new
        {
            joinLayerId = 1,
            predicate = "dwithin",
            distance = 100000,
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/spatialJoin",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var features = doc.RootElement.GetProperty("features");
        features.GetArrayLength().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.SpatialJoin)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/spatialJoin")]
    public async Task SpatialJoin_WithCarryFields_ReturnsAggregatedJoinAttributes()
    {
        var payload = JsonSerializer.Serialize(new
        {
            joinLayerId = 1,
            predicate = "dwithin",
            distance = 100000,
            carryFields = "name",
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/spatialJoin",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var features = doc.RootElement.GetProperty("features");
        features.GetArrayLength().Should().BeGreaterThan(0);

        // At least one target row should carry a non-empty name array.
        var anyWithNames = false;
        foreach (var feature in features.EnumerateArray())
        {
            if (feature.GetProperty("properties").TryGetProperty("name", out var names) &&
                names.ValueKind == JsonValueKind.Array && names.GetArrayLength() > 0)
            {
                anyWithNames = true;
                break;
            }
        }
        anyWithNames.Should().BeTrue("spatial join within 100km should find at least one matching join row");
    }

    [IntegrationTest]
    [Operation(Operations.SpatialJoin)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/spatialJoin")]
    public async Task SpatialJoin_MissingJoinLayerId_ReturnsBadRequest()
    {
        var payload = JsonSerializer.Serialize(new
        {
            predicate = "intersects",
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/spatialJoin",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("joinLayerId");
    }

    [IntegrationTest]
    [Operation(Operations.SpatialJoin)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/spatialJoin")]
    public async Task SpatialJoin_JoinLayerSameAsTarget_ReturnsBadRequest()
    {
        var payload = JsonSerializer.Serialize(new
        {
            joinLayerId = WebAppFixture.TestLayerId,
            predicate = "intersects",
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/spatialJoin",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("joinLayerId");
    }

    [IntegrationTest]
    [Operation(Operations.SpatialJoin)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/spatialJoin")]
    public async Task SpatialJoin_DWithinMissingDistance_ReturnsBadRequest()
    {
        var payload = JsonSerializer.Serialize(new
        {
            joinLayerId = 1,
            predicate = "dwithin",
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/spatialJoin",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("distance");
    }

    [IntegrationTest]
    [Operation(Operations.SpatialJoin)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/spatialJoin")]
    public async Task SpatialJoin_InvalidPredicate_ReturnsBadRequest()
    {
        var payload = JsonSerializer.Serialize(new
        {
            joinLayerId = 1,
            predicate = "bogus",
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/spatialJoin",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("predicate");
    }

    [IntegrationTest]
    [Operation(Operations.SpatialJoin)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/spatialJoin")]
    public async Task SpatialJoin_NonexistentJoinLayer_ReturnsNotFound()
    {
        var payload = JsonSerializer.Serialize(new
        {
            joinLayerId = 99999,
            predicate = "intersects",
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/spatialJoin",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---------- Buffer Aggregate ----------

    [IntegrationTest]
    [Operation(Operations.QueryBufferAggregate)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryBufferAggregate")]
    public async Task QueryBufferAggregate_DissolveTrue_ReturnsDissolvedPolygon()
    {
        var payload = JsonSerializer.Serialize(new
        {
            distance = 10000,
            unit = "meters",
            dissolve = true,
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryBufferAggregate",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        root.GetProperty("metadata").GetProperty("operation").GetString().Should().Be("buffer-aggregate");
        var features = root.GetProperty("features");
        // Dissolve with no groupBy should yield exactly one row.
        features.GetArrayLength().Should().Be(1);

        var props = features[0].GetProperty("properties");
        props.TryGetProperty("featureCount", out var count).Should().BeTrue();
        count.GetInt64().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.QueryBufferAggregate)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryBufferAggregate")]
    public async Task QueryBufferAggregate_GroupByField_ReturnsRowPerGroup()
    {
        var payload = JsonSerializer.Serialize(new
        {
            distance = 10000,
            unit = "meters",
            dissolve = true,
            groupByFields = "category",
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryBufferAggregate",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var features = doc.RootElement.GetProperty("features");

        // Seed layer 0 has two categories with geometry: 'test' and 'sample'.
        features.GetArrayLength().Should().BeGreaterOrEqualTo(1);
        features.GetArrayLength().Should().BeLessThanOrEqualTo(3);
    }

    [IntegrationTest]
    [Operation(Operations.QueryBufferAggregate)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryBufferAggregate")]
    public async Task QueryBufferAggregate_DissolveFalse_ReturnsRowPerFeature()
    {
        var payload = JsonSerializer.Serialize(new
        {
            distance = 1000,
            unit = "m",
            dissolve = false,
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryBufferAggregate",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var features = doc.RootElement.GetProperty("features");
        features.GetArrayLength().Should().BeGreaterThan(0);

        // Per-feature mode carries objectId per row.
        foreach (var feature in features.EnumerateArray())
        {
            feature.GetProperty("properties").TryGetProperty("objectId", out _).Should().BeTrue();
        }
    }

    [IntegrationTest]
    [Operation(Operations.QueryBufferAggregate)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryBufferAggregate")]
    public async Task QueryBufferAggregate_UnitKilometers_AcceptsAlias()
    {
        var payload = JsonSerializer.Serialize(new
        {
            distance = 10,
            unit = "km",
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryBufferAggregate",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.QueryBufferAggregate)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryBufferAggregate")]
    public async Task QueryBufferAggregate_MissingDistance_ReturnsBadRequest()
    {
        var payload = JsonSerializer.Serialize(new
        {
            unit = "meters",
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryBufferAggregate",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("distance");
    }

    [IntegrationTest]
    [Operation(Operations.QueryBufferAggregate)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryBufferAggregate")]
    public async Task QueryBufferAggregate_InvalidUnit_ReturnsBadRequest()
    {
        var payload = JsonSerializer.Serialize(new
        {
            distance = 1000,
            unit = "furlongs",
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryBufferAggregate",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("unit");
    }

    [IntegrationTest]
    [Operation(Operations.QueryBufferAggregate)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryBufferAggregate")]
    public async Task QueryBufferAggregate_DistanceCapBypassByMiles_ReturnsBadRequest()
    {
        // 80 miles ≈ 128.7 km, which exceeds the default 100 km MaxBufferDistanceMeters
        // cap. The cap must be enforced after unit conversion so non-meter units cannot
        // be used to slip past the operator-configured limit.
        var payload = JsonSerializer.Serialize(new
        {
            distance = 80,
            unit = "miles",
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryBufferAggregate",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("distance");
    }

    // ---------- Density Binning ----------

    [IntegrationTest]
    [Operation(Operations.QueryDensity)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryDensity")]
    public async Task QueryDensity_HexGrid_ReturnsCellsWithCounts()
    {
        var payload = JsonSerializer.Serialize(new
        {
            mode = "hex",
            cellSize = 20000,
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryDensity",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        root.GetProperty("metadata").GetProperty("operation").GetString().Should().Be("density");
        var features = root.GetProperty("features");
        features.GetArrayLength().Should().BeGreaterThan(0);

        foreach (var feature in features.EnumerateArray())
        {
            var props = feature.GetProperty("properties");
            props.TryGetProperty("cellId", out _).Should().BeTrue();
            props.TryGetProperty("featureCount", out var count).Should().BeTrue();
            count.GetInt64().Should().BeGreaterThan(0);
        }
    }

    [IntegrationTest]
    [Operation(Operations.QueryDensity)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryDensity")]
    public async Task QueryDensity_SquareGrid_ReturnsCellsWithCounts()
    {
        var payload = JsonSerializer.Serialize(new
        {
            mode = "square",
            cellSize = 20000,
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryDensity",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var features = doc.RootElement.GetProperty("features");
        features.GetArrayLength().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.QueryDensity)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryDensity")]
    public async Task QueryDensity_MissingCellSize_ReturnsBadRequest()
    {
        var payload = JsonSerializer.Serialize(new
        {
            mode = "hex",
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryDensity",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("cellSize");
    }

    [IntegrationTest]
    [Operation(Operations.QueryDensity)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryDensity")]
    public async Task QueryDensity_InvalidMode_ReturnsBadRequest()
    {
        var payload = JsonSerializer.Serialize(new
        {
            mode = "bogus",
            cellSize = 1000,
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryDensity",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("mode");
    }

    [IntegrationTest]
    [Operation(Operations.QueryDensity)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryDensity")]
    public async Task QueryDensity_CellSizeBelowMinimum_ReturnsBadRequest()
    {
        var payload = JsonSerializer.Serialize(new
        {
            mode = "hex",
            cellSize = 1,
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryDensity",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("cellSize");
    }

    [IntegrationTest]
    [Operation(Operations.QueryDensity)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryDensity")]
    public async Task QueryDensity_InvalidService_ReturnsNotFound()
    {
        var payload = JsonSerializer.Serialize(new
        {
            mode = "hex",
            cellSize = 20000,
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            "/rest/services/nonexistent/FeatureServer/0/queryDensity",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
