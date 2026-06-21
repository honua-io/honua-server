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
        foreach (var dataset in datasets.EnumerateArray())
        {
            if (string.Equals(dataset.GetProperty("key").GetString(), DatasetKey, StringComparison.OrdinalIgnoreCase))
            {
                dataset.GetProperty("category").GetString().Should().Be("boundary");
                dataset.GetProperty("defaultPredicate").GetString().Should().Be("intersects");
                found = true;
            }
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

        var response = await _fixture.Client.PostAsync(
            "/api/enrich",
            new StringContent(payload, Encoding.UTF8, "application/json"));

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

        var response = await _fixture.Client.PostAsync(
            "/api/enrich",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
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
        var response = await _fixture.Client.PostAsync(
            "/api/enrich",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.PaymentRequired);
    }
}
