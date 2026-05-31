// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;

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
    private readonly WebAppFixture _fixture = new();

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
}
