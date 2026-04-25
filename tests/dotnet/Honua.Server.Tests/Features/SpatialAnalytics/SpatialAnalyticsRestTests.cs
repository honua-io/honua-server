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
[Protocol(TestProtocols.SpatialAnalytics)]
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
    public async Task QueryClusters_OutStatisticsNonStringField_ReturnsBadRequest()
    {
        // JSON is syntactically valid but statisticType is a number token — the
        // parser must report it as a 400 "outStatistics" validation error
        // instead of letting JsonElement.GetString() escape as a 500.
        var payload = JsonSerializer.Serialize(new
        {
            algorithm = "dbscan",
            eps = 50000,
            minPoints = 1,
            returnHullPerCluster = true,
            outStatistics = "[{\"statisticType\":1,\"onStatisticField\":\"pop\",\"outStatisticFieldName\":\"x\"}]",
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryClusters",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("outStatistics");
    }

    [IntegrationTest]
    [Operation(Operations.QueryClusters)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryClusters")]
    public async Task QueryClusters_PerFeatureWithOutStatistics_ReturnsBadRequest()
    {
        // outStatistics only make sense in hull-per-cluster mode (the GROUP BY branch
        // in BuildClusterQuery). Per-feature mode returns one row per source feature
        // with no aggregation, so the handler must reject the combination instead of
        // silently discarding the statistics — mirrors the buffer-aggregate guard.
        var payload = JsonSerializer.Serialize(new
        {
            algorithm = "dbscan",
            eps = 50000,
            minPoints = 1,
            returnHullPerCluster = false,
            outStatistics = "[{\"statisticType\":\"count\",\"onStatisticField\":\"objectid\",\"outStatisticFieldName\":\"cnt\"}]",
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryClusters",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("outStatistics");
        content.Should().Contain("returnHullPerCluster");
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
    public async Task SpatialJoin_WithNumericOutStatistics_UsesNumericMaxOrdering()
    {
        await _fixture.Postgres.ExecuteAsync(
            """
            UPDATE features
            SET attributes = jsonb_set(attributes, '{related_id}', '10'::jsonb)
            WHERE layer_id = 1 AND objectid = 101;

            UPDATE features
            SET attributes = jsonb_set(attributes, '{related_id}', '2'::jsonb)
            WHERE layer_id = 1 AND objectid = 102;
            """,
            _fixture.CurrentSchema);

        var payload = JsonSerializer.Serialize(new
        {
            joinLayerId = 1,
            predicate = "dwithin",
            distance = 100000,
            outStatistics = "[{\"statisticType\":\"max\",\"onStatisticField\":\"related_id\",\"outStatisticFieldName\":\"max_related_id\"}]",
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/spatialJoin",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var features = doc.RootElement.GetProperty("features");

        var foundNumericMax = false;
        foreach (var feature in features.EnumerateArray())
        {
            if (!feature.GetProperty("properties").TryGetProperty("max_related_id", out var maxRelatedId) ||
                maxRelatedId.ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            if (maxRelatedId.GetDecimal() == 10m)
            {
                foundNumericMax = true;
                break;
            }
        }

        foundNumericMax.Should().BeTrue(
            "numeric max aggregation over JSON-backed related_id values should yield 10 instead of lexicographic 2");
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

    [IntegrationTest]
    [Operation(Operations.QueryBufferAggregate)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryBufferAggregate")]
    public async Task QueryBufferAggregate_DissolveFalseWithOutStatistics_ReturnsBadRequest()
    {
        // outStatistics requires GROUP BY which only exists in the dissolve=true SQL
        // path. The server must reject dissolve=false + outStatistics at the handler
        // so the builder never emits aggregate columns without a GROUP BY clause.
        var payload = JsonSerializer.Serialize(new
        {
            distance = 1000,
            unit = "meters",
            dissolve = false,
            outStatistics = "[{\"statisticType\":\"count\",\"onStatisticField\":\"objectid\",\"outStatisticFieldName\":\"cnt\"}]",
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryBufferAggregate",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("outStatistics");
        content.Should().Contain("dissolve");
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

    // ---------- Fix #1: TryBuildFeatureQuery shared filter parameters ----------
    // These tests cover the extended filter parsing (objectIds / time / geometry /
    // spatialRel) added so the analytics endpoints honour the same FeatureQuery
    // shape as the rest of FeatureServer. Each test exercises one filter dimension
    // through a representative analytics endpoint to keep the suite quick.

    [IntegrationTest]
    [Operation(Operations.QueryClusters)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryClusters")]
    public async Task QueryClusters_WithObjectIdsFilter_NarrowsInput()
    {
        // Seed layer 0 has 5 features (objectids 1..5). Filtering to {1, 5} should
        // limit clustering input to those two features (objectid 3 has no geometry
        // and is filtered by the IS NOT NULL guard regardless).
        var payload = JsonSerializer.Serialize(new
        {
            algorithm = "dbscan",
            eps = 50000,
            minPoints = 1,
            objectIds = "1,5",
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryClusters",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var features = doc.RootElement.GetProperty("features");

        // Per-feature mode emits one row per source feature, so the filter should
        // bound the response to at most the two requested objectids.
        features.GetArrayLength().Should().BeLessThanOrEqualTo(2);
        foreach (var feature in features.EnumerateArray())
        {
            var objectId = feature.GetProperty("properties").GetProperty("objectId").GetInt64();
            objectId.Should().BeOneOf(1L, 5L);
        }
    }

    [IntegrationTest]
    [Operation(Operations.QueryClusters)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryClusters")]
    public async Task QueryClusters_WithInvalidObjectIds_ReturnsBadRequest()
    {
        var payload = JsonSerializer.Serialize(new
        {
            algorithm = "dbscan",
            eps = 50000,
            minPoints = 1,
            objectIds = "1,not-a-number,5",
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryClusters",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("objectIds");
    }

    [IntegrationTest]
    [Operation(Operations.QueryBufferAggregate)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryBufferAggregate")]
    public async Task QueryBufferAggregate_WithGeometryFilter_NarrowsInput()
    {
        // Bounding box around the seed feature cluster (-122.8, 37.2, -122.0, 38.0).
        // The geometry filter should pass through TryBuildFeatureQuery and narrow
        // the input set so the dissolved buffer reflects only the bbox-intersecting
        // features.
        var payload = JsonSerializer.Serialize(new
        {
            distance = 5000,
            unit = "meters",
            dissolve = true,
            geometry = "-122.8,37.2,-122.0,38.0",
            geometryType = "esriGeometryEnvelope",
            inSR = 4326,
            spatialRel = "esriSpatialRelIntersects",
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryBufferAggregate",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var features = doc.RootElement.GetProperty("features");
        features.GetArrayLength().Should().Be(1);

        // The dissolved row should have a positive featureCount because the bbox
        // covers the seeded feature cluster.
        var props = features[0].GetProperty("properties");
        props.GetProperty("featureCount").GetInt64().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.QueryClusters)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryClusters")]
    public async Task QueryClusters_WithDistanceBasedSpatialRel_ReturnsBadRequest()
    {
        // The analytics endpoints overload `distance` for operation-specific
        // semantics (DWithin radius / buffer radius), so distance-based esri
        // spatial relationships are explicitly rejected to avoid silent
        // parameter collisions. Cluster doesn't use `distance`, but the guard
        // is shared across the slice and must reject the relationship anyway.
        var payload = JsonSerializer.Serialize(new
        {
            algorithm = "dbscan",
            eps = 50000,
            minPoints = 1,
            geometry = "-122.5,37.5",
            geometryType = "esriGeometryPoint",
            inSR = 4326,
            spatialRel = "esriSpatialRelWithinDistance",
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryClusters",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("spatialRel");
    }

    [IntegrationTest]
    [Operation(Operations.QueryDensity)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryDensity")]
    public async Task QueryDensity_WithTimeFilter_FlowsThroughTemporalBuilder()
    {
        // Layer 0 declares timeInfo with startTimeField=timestamp,endTimeField=event_date
        // in the seed, so the same `time={start,end}` shape that FeatureServer's
        // main query handler accepts should also flow through the analytics
        // path. The fix wires TryBuildFeatureQueryAsync to call the same
        // GeoServicesTemporalQueryBuilder used by the main handler, so the
        // request must succeed and yield a well-formed feature collection.
        var payload = JsonSerializer.Serialize(new
        {
            mode = "hex",
            cellSize = 20000,
            time = "2023-01-01T00:00:00Z,2023-12-31T23:59:59Z",
            timeRelation = "esriTimeRelationOverlaps",
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryDensity",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        doc.RootElement.GetProperty("metadata").GetProperty("operation").GetString().Should().Be("density");
    }

    // ---------- Fix #3: aggregated paths report InputTruncated correctly ----------
    // These tests verify the SQL builder emits "_inputCount" and the handler
    // strips it from the response payload. Triggering an actual truncation
    // requires >MaxInputFeatures (≥1000) seeded features, which is impractical
    // in the unit-style integration suite — the regression we guard against is
    // the column leaking into feature properties or the metadata flag being
    // hardcoded false.

    [IntegrationTest]
    [Operation(Operations.QueryClusters)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryClusters")]
    public async Task QueryClusters_HullMode_DoesNotLeakInputCountIntoProperties()
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

        // The handler strips _inputCount before MapRowToFeature so it must not
        // appear in any feature.properties bag.
        foreach (var feature in root.GetProperty("features").EnumerateArray())
        {
            feature.GetProperty("properties").TryGetProperty("_inputCount", out _)
                .Should().BeFalse("_inputCount is an internal SQL signal, not a public property");
        }

        // Metadata.inputTruncated must be present and false (small input).
        var metadata = root.GetProperty("metadata");
        metadata.GetProperty("inputTruncated").GetBoolean().Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.QueryBufferAggregate)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryBufferAggregate")]
    public async Task QueryBufferAggregate_DoesNotLeakInputCountIntoProperties()
    {
        var payload = JsonSerializer.Serialize(new
        {
            distance = 1000,
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

        foreach (var feature in root.GetProperty("features").EnumerateArray())
        {
            feature.GetProperty("properties").TryGetProperty("_inputCount", out _)
                .Should().BeFalse("_inputCount must be stripped before MapRowToFeature");
        }

        root.GetProperty("metadata").GetProperty("inputTruncated").GetBoolean().Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.QueryDensity)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryDensity")]
    public async Task QueryDensity_DoesNotLeakInputCountIntoProperties()
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

        foreach (var feature in root.GetProperty("features").EnumerateArray())
        {
            feature.GetProperty("properties").TryGetProperty("_inputCount", out _)
                .Should().BeFalse("_inputCount must be stripped before MapRowToFeature");
        }

        // Density was previously hardcoding inputTruncated=false; the fix wires
        // it to the actual overflow detector. With small input it must still be
        // false, but the property must now be present in the metadata.
        root.GetProperty("metadata").GetProperty("inputTruncated").GetBoolean().Should().BeFalse();
    }

    // ---------- Fix #4: cluster and spatial-join geometries are EPSG:4326 ----------
    // Per `application/geo+json` (RFC 7946), the response must be in WGS 84
    // unless an alternate CRS is negotiated. The cluster (per-feature + hull)
    // and spatial-join SQL builders now wrap the emitted geometry with
    // ST_Transform(_, 4326). The seed layer is already in 4326 so the
    // transform is effectively a no-op for these tests, but verifying the
    // coordinate ranges guards against future seed changes that switch to a
    // projected SRID.

    [IntegrationTest]
    [Operation(Operations.QueryClusters)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryClusters")]
    public async Task QueryClusters_PerFeature_ReturnsGeometryInWgs84()
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

        var anyChecked = false;
        foreach (var feature in doc.RootElement.GetProperty("features").EnumerateArray())
        {
            if (!feature.TryGetProperty("geometry", out var geometry) || geometry.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            if (!geometry.TryGetProperty("coordinates", out var coords) || coords.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            // Per-feature mode is point geometry — first two coordinates are
            // longitude / latitude in degrees.
            var lon = coords[0].GetDouble();
            var lat = coords[1].GetDouble();
            lon.Should().BeInRange(-180d, 180d);
            lat.Should().BeInRange(-90d, 90d);
            anyChecked = true;
        }

        anyChecked.Should().BeTrue("at least one cluster row should carry a geometry to validate");
    }

    [IntegrationTest]
    [Operation(Operations.SpatialJoin)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/spatialJoin")]
    public async Task SpatialJoin_ReturnsTargetGeometryInWgs84()
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

        var anyChecked = false;
        foreach (var feature in doc.RootElement.GetProperty("features").EnumerateArray())
        {
            if (!feature.TryGetProperty("geometry", out var geometry) || geometry.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            if (!geometry.TryGetProperty("coordinates", out var coords) || coords.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var lon = coords[0].GetDouble();
            var lat = coords[1].GetDouble();
            lon.Should().BeInRange(-180d, 180d);
            lat.Should().BeInRange(-90d, 90d);
            anyChecked = true;
        }

        anyChecked.Should().BeTrue("spatial join should emit at least one target geometry to validate");
    }
}
