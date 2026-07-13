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
    public async Task Enrich_PointInPolygonMethod_ReturnsFeatureCollection()
    {
        var payload = JsonSerializer.Serialize(new
        {
            datasetKey = DatasetKey,
            sourceLayerId = WebAppFixture.TestLayerId,
            method = "point-in-polygon",
        });

        using var requestContent = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync("/api/enrich", requestContent);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        doc.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");
    }

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
