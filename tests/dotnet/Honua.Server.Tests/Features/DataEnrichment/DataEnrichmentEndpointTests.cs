// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.DataEnrichment;

/// <summary>
/// Endpoint-level integration tests for the data-enrichment API (#374). The
/// fixture registers an enrichment dataset that points at the seed join layer
/// (layer 1) so the spatial-join enrichment runs against a real PostGIS database,
/// and runs as Pro edition so the entitlement gate passes (the gate denial path is
/// covered separately).
/// </summary>
[Collection("Database")]
[Protocol(ProtocolNames.DataEnrichment)]
public sealed class DataEnrichmentEndpointTests : IAsyncLifetime
{
    private const string DatasetKey = "test-boundaries";

    // Containment fixture object ids (honua-server#3069), outside the seeded ranges.
    private const int PointInsideObjectId = 7701;
    private const int PointOutsideObjectId = 7702;
    private const int PolygonContainerObjectId = 7703;

    private readonly WebAppFixture _fixture = new WebAppFixture()
        .WithTestLicense(HonuaEdition.Pro)
        .ConfigureWebHost(builder => builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataEnrichment:Datasets:0:Key"] = DatasetKey,
                ["DataEnrichment:Datasets:0:DisplayName"] = "Test Boundaries",
                ["DataEnrichment:Datasets:0:Category"] = "boundary",
                ["DataEnrichment:Datasets:0:LayerId"] = "1",
                ["DataEnrichment:Datasets:0:Predicate"] = "intersects",
            });
        }));

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.EnrichCatalog)]
    [Endpoint("GET /api/enrich/catalog")]
    public async Task Catalog_ListsRegisteredDataset()
    {
        var response = await _fixture.Client.GetAsync("/api/enrich/catalog");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var datasets = doc.RootElement.GetProperty("datasets");
        datasets.GetArrayLength().Should().BeGreaterThan(0);

        var found = false;
        foreach (var dataset in datasets.EnumerateArray()
            .Where(d => string.Equals(d.GetProperty("key").GetString(), DatasetKey, StringComparison.OrdinalIgnoreCase)))
        {
            dataset.GetProperty("category").GetString().Should().Be("boundary");
            dataset.GetProperty("defaultPredicate").GetString().Should().Be("intersects");
            found = true;
        }

        found.Should().BeTrue("the registered enrichment dataset must appear in the catalog");
    }

    [IntegrationTest]
    [Operation(Operations.Enrich)]
    [Endpoint("POST /api/enrich")]
    public async Task Enrich_Intersects_ReturnsSourceFeaturesWithMatchCount()
    {
        var payload = JsonSerializer.Serialize(new
        {
            datasetKey = DatasetKey,
            sourceLayerId = WebAppFixture.TestLayerId,
            predicate = "intersects",
        });

        using var requestContent = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync("/api/enrich", requestContent);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        root.GetProperty("type").GetString().Should().Be("FeatureCollection");
        root.GetProperty("metadata").GetProperty("operation").GetString().Should().Be("enrich");

        var features = root.GetProperty("features");
        features.GetArrayLength().Should().BeGreaterThan(0);

        foreach (var feature in features.EnumerateArray())
        {
            feature.GetProperty("type").GetString().Should().Be("Feature");
            var props = feature.GetProperty("properties");
            props.TryGetProperty("matchCount", out var matchCount).Should().BeTrue();
            matchCount.GetInt64().Should().BeGreaterOrEqualTo(0);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Enrich)]
    [Endpoint("POST /api/enrich")]
    public async Task Enrich_UnknownDataset_ReturnsNotFound()
    {
        var payload = JsonSerializer.Serialize(new
        {
            datasetKey = "does-not-exist",
            sourceLayerId = WebAppFixture.TestLayerId,
        });

        using var requestContent = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync("/api/enrich", requestContent);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Enrich)]
    [Endpoint("POST /api/enrich")]
    public async Task Enrich_PointInPolygonMethod_MatchesOnlySourcePointsInsideTheDatasetPolygon()
    {
        // honua-server#3069: this assertion used to be "HTTP 200 and type ==
        // FeatureCollection", which passed while the SQL path evaluated
        // ST_Contains(sourcePoint, datasetPolygon) — always false — so synchronous
        // point-in-polygon silently returned zero matches for every caller. The
        // containment direction is now proven by the match counts themselves.
        await SeedContainmentFixtureAsync();

        var payload = JsonSerializer.Serialize(new
        {
            datasetKey = DatasetKey,
            sourceLayerId = WebAppFixture.TestLayerId,
            method = "point-in-polygon",
            where = "category = 'pip-source'",
            outputFields = new[] { "description" },
        });

        using var requestContent = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync("/api/enrich", requestContent);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        doc.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");

        var byObjectId = IndexByObjectId(doc.RootElement);
        byObjectId.Should().HaveCount(2, "the where filter narrows the source rows to the two fixture points");

        byObjectId[PointInsideObjectId].GetProperty("matchCount").GetInt64()
            .Should().Be(1, "the source point falls inside the enrichment dataset polygon");
        byObjectId[PointInsideObjectId].GetProperty("description").EnumerateArray()
            .Select(value => value.GetString())
            .Should().BeEquivalentTo(["pip-zone"], "the containing dataset polygon's attributes are carried onto the point");

        byObjectId[PointOutsideObjectId].GetProperty("matchCount").GetInt64()
            .Should().Be(0, "the source point outside the polygon must not be enriched");
    }

    [IntegrationTest]
    [Operation(Operations.Enrich)]
    [Endpoint("POST /api/enrich")]
    public async Task Enrich_WithinMethod_MatchesDatasetFeaturesInsideTheSourcePolygon()
    {
        // The inverse containment direction (honua-server#3069): `within` is
        // dataset-subject too, so it matches dataset features that sit inside the
        // caller's source geometry. Asserting both directions with the same fixture
        // proves the operands are not simply symmetric.
        await SeedContainmentFixtureAsync();

        using var within = JsonDocument.Parse(await EnrichAsync("within"));
        var withinByObjectId = IndexByObjectId(within.RootElement);
        withinByObjectId.Should().HaveCount(1, "the where filter narrows the source rows to the container polygon");
        withinByObjectId[PolygonContainerObjectId].GetProperty("matchCount").GetInt64()
            .Should().Be(1, "exactly one dataset point sits inside the source polygon");
        withinByObjectId[PolygonContainerObjectId].GetProperty("description").EnumerateArray()
            .Select(value => value.GetString())
            .Should().BeEquivalentTo(["pip-poi"]);

        using var pointInPolygon = JsonDocument.Parse(await EnrichAsync("point-in-polygon"));
        var pipByObjectId = IndexByObjectId(pointInPolygon.RootElement);
        pipByObjectId[PolygonContainerObjectId].GetProperty("matchCount").GetInt64()
            .Should().Be(0, "the dataset point does not contain the source polygon, so the opposite direction finds nothing");

        async Task<string> EnrichAsync(string method)
        {
            var payload = JsonSerializer.Serialize(new
            {
                datasetKey = DatasetKey,
                sourceLayerId = WebAppFixture.TestLayerId,
                method,
                where = "category = 'pip-container'",
                outputFields = new[] { "description" },
            });

            using var requestContent = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await _fixture.Client.PostAsync("/api/enrich", requestContent);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            return await response.Content.ReadAsStringAsync();
        }
    }

    // Deterministic containment fixture shared by the point-in-polygon / within
    // assertions: two source points (one inside, one outside a dataset polygon) plus
    // a source polygon that contains a dataset point. All coordinates are far away
    // from the seeded features so no seed row can satisfy either predicate.
    private Task SeedContainmentFixtureAsync()
        => _fixture.Postgres.ExecuteAsync(
            $"""
            DELETE FROM features WHERE objectid IN ({PointInsideObjectId}, {PointOutsideObjectId}, {PolygonContainerObjectId}, 7801, 7802);

            INSERT INTO features (objectid, layer_id, geometry, attributes) VALUES
                ({PointInsideObjectId}, {WebAppFixture.TestLayerId}, ST_SetSRID(ST_MakePoint(10.5, 10.5), 4326),
                    jsonb_build_object('objectid', {PointInsideObjectId}, 'name', 'pip-inside', 'category', 'pip-source')),
                ({PointOutsideObjectId}, {WebAppFixture.TestLayerId}, ST_SetSRID(ST_MakePoint(20.5, 20.5), 4326),
                    jsonb_build_object('objectid', {PointOutsideObjectId}, 'name', 'pip-outside', 'category', 'pip-source')),
                ({PolygonContainerObjectId}, {WebAppFixture.TestLayerId},
                    ST_SetSRID(ST_GeomFromText('POLYGON((30 30, 31 30, 31 31, 30 31, 30 30))'), 4326),
                    jsonb_build_object('objectid', {PolygonContainerObjectId}, 'name', 'pip-container', 'category', 'pip-container'));

            INSERT INTO features (objectid, layer_id, geometry, attributes) VALUES
                (7801, 1, ST_SetSRID(ST_GeomFromText('POLYGON((10 10, 11 10, 11 11, 10 11, 10 10))'), 4326),
                    jsonb_build_object('objectid', 7801, 'name', 'pip-zone', 'description', 'pip-zone')),
                (7802, 1, ST_SetSRID(ST_MakePoint(30.5, 30.5), 4326),
                    jsonb_build_object('objectid', 7802, 'name', 'pip-poi', 'description', 'pip-poi'));
            """,
            _fixture.CurrentSchema);

    private static Dictionary<long, JsonElement> IndexByObjectId(JsonElement root)
        => root.GetProperty("features")
            .EnumerateArray()
            .Select(feature => feature.GetProperty("properties"))
            .ToDictionary(properties => properties.GetProperty("objectId").GetInt64(), properties => properties);

    [IntegrationTest]
    [Operation(Operations.Enrich)]
    [Endpoint("POST /api/enrich")]
    public async Task Enrich_WithAggregates_ReturnsAggregateProperty()
    {
        var payload = JsonSerializer.Serialize(new
        {
            datasetKey = DatasetKey,
            sourceLayerId = WebAppFixture.TestLayerId,
            method = "intersects",
            aggregates = new[]
            {
                new { statisticType = "count", onField = "name", outName = "count_name" },
            },
        });

        using var requestContent = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync("/api/enrich", requestContent);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var features = doc.RootElement.GetProperty("features");
        features.GetArrayLength().Should().BeGreaterThan(0);
        features.EnumerateArray().First()
            .GetProperty("properties").TryGetProperty("count_name", out _)
            .Should().BeTrue("the aggregate output field must be present on enriched features");
    }

    [IntegrationTest]
    [Operation(Operations.Enrich)]
    [Endpoint("POST /api/enrich")]
    public async Task Enrich_NearestNeighborMethod_ReturnsNotImplemented()
    {
        var payload = JsonSerializer.Serialize(new
        {
            datasetKey = DatasetKey,
            sourceLayerId = WebAppFixture.TestLayerId,
            method = "nearest-neighbor",
        });

        using var requestContent = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync("/api/enrich", requestContent);

        response.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
    }

    [IntegrationTest]
    [Operation(Operations.Enrich)]
    [Endpoint("POST /api/enrich")]
    public async Task Enrich_InlineGeoJsonSource_ReturnsNotImplemented()
    {
        var payload = JsonSerializer.Serialize(new
        {
            datasetKey = DatasetKey,
            sourceLayerId = WebAppFixture.TestLayerId,
            features = new { type = "FeatureCollection", features = Array.Empty<object>() },
        });

        using var requestContent = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync("/api/enrich", requestContent);

        response.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
    }
}

/// <summary>
/// Verifies the synchronous over-limit guard (#2282): when the source selection
/// exceeds the configured analytics input cap, the sync endpoint returns 413 and
/// points callers at the async batch path. Configures a cap of 1 so the seed layer
/// trips the guard.
/// </summary>
[Collection("Database")]
[Protocol(ProtocolNames.DataEnrichment)]
public sealed class DataEnrichmentSyncLimitTests : IAsyncLifetime
{
    private const string DatasetKey = "test-boundaries";

    private readonly WebAppFixture _fixture = new WebAppFixture()
        .WithTestLicense(HonuaEdition.Pro)
        .ConfigureWebHost(builder => builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataEnrichment:Datasets:0:Key"] = DatasetKey,
                ["DataEnrichment:Datasets:0:LayerId"] = "1",
                ["DataEnrichment:Datasets:0:Predicate"] = "intersects",
                ["Limits:Analytics:MaxInputFeatures"] = "1",
            });
        }));

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Enrich)]
    [Endpoint("POST /api/enrich")]
    public async Task Enrich_OverLimit_ReturnsPayloadTooLarge()
    {
        var payload = JsonSerializer.Serialize(new
        {
            datasetKey = DatasetKey,
            sourceLayerId = WebAppFixture.TestLayerId,
            method = "intersects",
        });

        using var requestContent = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync("/api/enrich", requestContent);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }
}

/// <summary>
/// Verifies the data-enrichment endpoints are gated by the Pro entitlement. Runs as
/// Community edition so both routes return HTTP 402.
/// </summary>
[Collection("Database")]
[Protocol(ProtocolNames.DataEnrichment)]
public sealed class DataEnrichmentEditionGateTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .WithTestLicense(HonuaEdition.Community);

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.EnrichCatalog)]
    [Endpoint("GET /api/enrich/catalog")]
    public async Task Catalog_CommunityEdition_ReturnsPaymentRequired()
    {
        var response = await _fixture.Client.GetAsync("/api/enrich/catalog");
        response.StatusCode.Should().Be(HttpStatusCode.PaymentRequired);
    }

    [IntegrationTest]
    [Operation(Operations.Enrich)]
    [Endpoint("POST /api/enrich")]
    public async Task Enrich_CommunityEdition_ReturnsPaymentRequired()
    {
        var payload = JsonSerializer.Serialize(new { datasetKey = "x", sourceLayerId = 0 });
        using var requestContent = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync("/api/enrich", requestContent);
        response.StatusCode.Should().Be(HttpStatusCode.PaymentRequired);
    }
}
