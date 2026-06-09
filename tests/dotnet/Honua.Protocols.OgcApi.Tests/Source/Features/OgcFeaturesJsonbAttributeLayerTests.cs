// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

/// <summary>
/// Regression for honua-server#1238: querying a JSONB-attribute-backed layer (mobile
/// offline demo layer 68910) via OGC API Features <c>/items</c> must succeed only because
/// the storage binding declares <c>attributesColumn=attributes</c>. Without that accessor
/// the storage-mapped reader projects bare columns against the shared <c>features</c> table
/// (which has none) and Postgres raises 42703 — surfacing as an OGC 500. This test
/// reproduces the broken drift, then proves the seed/bridge fix resolves it.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.OgcApiFeatures)]
[Operation(Operations.Query)]
public sealed class OgcFeaturesJsonbAttributeLayerTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();

        var schema = _fixture.CurrentSchema
            ?? throw new InvalidOperationException("WebAppFixture did not initialize an isolated schema.");

        var seedPath = RepositoryPaths.Resolve("tests", "seed", "mobile-offline-demo-v1.sql");
        var seedSql = await File.ReadAllTextAsync(seedPath);

        await using var connection = await _fixture.Postgres.GetConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        command.CommandText = seedSql;
        await command.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    private TestMetadataV2GraphProvider Provider
        => _fixture.GetService<TestMetadataV2GraphProvider>()
            ?? throw new InvalidOperationException(
                "Test V2 graph provider not registered as TestMetadataV2GraphProvider.");

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/68910/items")]
    public async Task GetItems_JsonbAttributes_RequireStorageAccessor()
    {
        var requestUri =
            $"/ogc/features/collections/{MobileOfflineDemoGraphPublisher.OfflineSitesLayerId}/items";

        // Reproduce the drift: no attributesColumn => bare-column projection => 42703 => 500.
        MobileOfflineDemoGraphPublisher.Publish(Provider, includeAttributesAccessor: false);

        var brokenResponse = await _fixture.Client.GetAsync(requestUri);
        brokenResponse.StatusCode.Should().NotBe(HttpStatusCode.OK,
            "without the JSONB accessor the reader projects bare columns and Postgres "
            + "rejects the query with 42703 (the honua-server#1238 failure)");

        // The fix: with the accessor, declared fields resolve as attributes->>'field'.
        MobileOfflineDemoGraphPublisher.Publish(Provider, includeAttributesAccessor: true);

        var fixedResponse = await _fixture.Client.GetAsync(requestUri);
        fixedResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "with attributesColumn=attributes the JSONB-backed layer is queryable via OGC items");

        var json = JsonDocument.Parse(await fixedResponse.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");
        json.RootElement.GetProperty("features").GetArrayLength().Should().BeGreaterThanOrEqualTo(3,
            "the v1 seed inserts three deterministic offline-site features");
    }

    /// <summary>
    /// Regression for honua-server#1238 follow-up (post-merge Codex P1): layers 68910 (3 point
    /// features) and 68920 (2 polygon features) both live in the shared <c>features</c> table,
    /// discriminated by <c>layer_id</c>. A query for one layer must return only that layer's
    /// rows — never the other layer's features stored in the same table. We assert isolation via
    /// both the OGC API Features <c>/items</c> and the GeoServices FeatureServer <c>/query</c>
    /// surfaces, since both adapt through the storage-mapped reader.
    /// </summary>
    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/68910/items")]
    public async Task GetItems_SharedFeaturesTable_IsolatesByLayerDiscriminator()
    {
        MobileOfflineDemoGraphPublisher.Publish(Provider, includeAttributesAccessor: true);

        // OGC API Features: layer 68910 returns its 3 site features only (not the 2 zones).
        await AssertLayerIsolatedAsync(
            $"/ogc/features/collections/{MobileOfflineDemoGraphPublisher.OfflineSitesLayerId}/items",
            "features",
            expectedCount: 3,
            presentField: "site_name",
            absentField: "zone_name");

        // OGC API Features: layer 68920 returns its 2 zone features only (not the 3 sites).
        await AssertLayerIsolatedAsync(
            $"/ogc/features/collections/{MobileOfflineDemoGraphPublisher.OfflineZonesLayerId}/items",
            "features",
            expectedCount: 2,
            presentField: "zone_name",
            absentField: "site_name");

        // FeatureServer /query: same isolation through the GeoServices adapter.
        await AssertLayerIsolatedAsync(
            $"/rest/services/{MobileOfflineDemoGraphPublisher.ServiceName}/FeatureServer/{MobileOfflineDemoGraphPublisher.OfflineSitesLayerId}/query?where=1%3D1&outFields=*&f=geojson",
            "features",
            expectedCount: 3,
            presentField: "site_name",
            absentField: "zone_name");

        await AssertLayerIsolatedAsync(
            $"/rest/services/{MobileOfflineDemoGraphPublisher.ServiceName}/FeatureServer/{MobileOfflineDemoGraphPublisher.OfflineZonesLayerId}/query?where=1%3D1&outFields=*&f=geojson",
            "features",
            expectedCount: 2,
            presentField: "zone_name",
            absentField: "site_name");
    }

    private async Task AssertLayerIsolatedAsync(
        string requestUri,
        string featuresProperty,
        int expectedCount,
        string presentField,
        string absentField)
    {
        var response = await _fixture.Client.GetAsync(requestUri);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {requestUri} should succeed");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var features = json.RootElement.GetProperty(featuresProperty);
        features.GetArrayLength().Should().Be(expectedCount,
            $"only the requested layer's rows must be returned by {requestUri}");

        foreach (var feature in features.EnumerateArray())
        {
            var properties = feature.TryGetProperty("properties", out var props)
                ? props
                : feature.GetProperty("attributes");

            properties.TryGetProperty(presentField, out _).Should().BeTrue(
                $"every returned feature must belong to the requested layer (has '{presentField}')");
            properties.TryGetProperty(absentField, out _).Should().BeFalse(
                $"no feature from the other layer (carrying '{absentField}') may leak across the shared table");
        }
    }
}
