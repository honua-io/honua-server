// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Tiles;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.VectorTileServer;

/// <summary>
/// root.json and the tile route must resolve the SAME primary publication on a
/// multi-publication VectorTileServer service (honua-server#4112). Each storage layer's tile
/// provider returns distinct bytes and each resource's stored style carries a distinct name,
/// so the assertions identify exactly which publication each handler picked.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.VectorTileServer)]
public sealed class VectorTileServerPrimaryPublicationTests :
    IClassFixture<VectorTileServerPrimaryPublicationTests.Fixture>
{
    private const int MixedFeatureStorageId = 61;
    private const int MixedVectorStorageId = 62;
    private const int RestrictedOpenStorageId = 63;
    private const int RestrictedPrivateStorageId = 64;

    private readonly Fixture _fixture;

    public VectorTileServerPrimaryPublicationTests(Fixture fixture) => _fixture = fixture;

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/tile/{z}/{y}/{x}.pbf")]
    public async Task VectorTileServer_PrimaryFeatureLayerAndVectorTileLayer_StyleAndTileUseVectorTileLayer()
    {
        // Publication A is an EsriFeatureLayer flagged IsPrimary at layer index 0; publication B
        // is the EsriVectorTileLayer at layer index 1. The adapter prefers the vector tile
        // publication, so both the style and the tile must come from B.
        using var client = _fixture.App.CreateClient();

        var styleName = await GetRootStyleNameAsync(client, "vts-mixed");
        var tile = await GetTileAsync(client, "vts-mixed");

        styleName.Should().Be("mixed-vector-layer-style");
        tile.Should().Equal(TileBytes(MixedVectorStorageId));
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/tile/{z}/{y}/{x}.pbf")]
    public async Task VectorTileServer_AnonymousCallerWithoutAccessToVectorTileLayer_StyleAndTileUseSameAccessiblePublication()
    {
        // The EsriVectorTileLayer publication's resource denies anonymous access. An anonymous
        // caller must get the style AND the tiles of the next accessible publication (the
        // primary feature layer), never a style for one layer over tiles of another.
        using var client = _fixture.App.CreateClient();

        var styleName = await GetRootStyleNameAsync(client, "vts-restricted");
        var tile = await GetTileAsync(client, "vts-restricted");

        styleName.Should().Be("restricted-open-layer-style");
        tile.Should().Equal(TileBytes(RestrictedOpenStorageId));
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/tile/{z}/{y}/{x}.pbf")]
    public async Task VectorTileServer_AuthenticatedCallerWithAccessToVectorTileLayer_StyleAndTileUseVectorTileLayer()
    {
        using var client = _fixture.App.CreateAdminClient();

        var styleName = await GetRootStyleNameAsync(client, "vts-restricted");
        var tile = await GetTileAsync(client, "vts-restricted");

        styleName.Should().Be("restricted-private-layer-style");
        tile.Should().Equal(TileBytes(RestrictedPrivateStorageId));
    }

    private static async Task<string?> GetRootStyleNameAsync(HttpClient client, string serviceName)
    {
        var response = await client.GetAsync(
            $"/rest/services/{serviceName}/VectorTileServer/resources/styles/root.json");
        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        using var style = JsonDocument.Parse(content);
        var tiles = style.RootElement.GetProperty("sources").GetProperty("esri").GetProperty("tiles");
        tiles.EnumerateArray().Should().ContainSingle()
            .Which.GetString().Should().EndWith($"/rest/services/{serviceName}/VectorTileServer/tile/{{z}}/{{y}}/{{x}}.pbf");
        return style.RootElement.GetProperty("name").GetString();
    }

    private static async Task<byte[]> GetTileAsync(HttpClient client, string serviceName)
    {
        var response = await client.GetAsync($"/rest/services/{serviceName}/VectorTileServer/tile/1/0/0.pbf");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadAsByteArrayAsync();
    }

    private static byte[] TileBytes(int storageLayerId) => [0x1A, (byte)storageLayerId];

    public sealed class Fixture : IAsyncLifetime
    {
        public WebAppFixture App { get; }

        public Fixture()
        {
            var tileProvider = Substitute.For<ITileProvider>();
            foreach (var storageLayerId in new[]
                     {
                         MixedFeatureStorageId, MixedVectorStorageId, RestrictedOpenStorageId, RestrictedPrivateStorageId
                     })
            {
                tileProvider.GetMvtTileAsync(
                        Arg.Is(storageLayerId), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<FeatureQuery?>(),
                        Arg.Any<TileOptions>(), Arg.Any<TileLimits>(), Arg.Any<GridGeometry?>(), Arg.Any<CancellationToken>())
                    .Returns(TileBytes(storageLayerId));
            }

            var styleProjection = Substitute.For<IOgcStyleProjection>();
            styleProjection.GetStylesheetAsync(
                    Arg.Any<string>(), OgcStyleEncoding.MapboxStyle, Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var styleId = (string)call[0];
                    var json = JsonSerializer.Serialize(new
                    {
                        version = 8,
                        name = $"{styleId}-style",
                        sources = new { },
                        layers = Array.Empty<object>()
                    });
                    return new OgcStylesheet(json, "application/vnd.mapbox.style+json", OgcStyleEncoding.MapboxStyle);
                });

            var graphProvider = BuildGraphProvider();
            App = new WebAppFixture()
                // Displace the development-authentication bypass so a credential-less client is
                // genuinely anonymous; the admin client still authenticates with X-API-Key.
                .ConfigureWebHost(builder =>
                {
                    builder.UseSetting("HONUA_DEV_AUTH", "false");
                    builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
                })
                .ReplaceService<ITileProvider>(tileProvider)
                .ReplaceService<IOgcStyleProjection>(styleProjection)
                .ConfigureServices(services =>
                {
                    services.RemoveAll<IMetadataV2GraphProvider>();
                    services.AddSingleton<IMetadataV2GraphProvider>(graphProvider);
                });
        }

        public Task InitializeAsync() => App.InitializeAsync();

        public Task DisposeAsync() => App.DisposeAsync();

        private static TestMetadataV2GraphProvider BuildGraphProvider()
        {
            var openPolicy = new AccessPolicy { AllowAnonymous = true };
            var privatePolicy = new AccessPolicy { AllowAnonymous = false };

            return new TestMetadataV2GraphBuilder()
                .AddResource("res-mixed-feature", "mixed-feature-layer", accessPolicy: openPolicy)
                .AddStorageBinding("binding-mixed-feature", "res-mixed-feature", "features", storageLayerId: MixedFeatureStorageId)
                .AddResource("res-mixed-vector", "mixed-vector-layer", accessPolicy: openPolicy)
                .AddStorageBinding("binding-mixed-vector", "res-mixed-vector", "features", storageLayerId: MixedVectorStorageId)
                .AddService("svc-vts-mixed", "vts-mixed", protocols: [ServiceProtocols.VectorTileServer], accessPolicy: openPolicy)
                .AddPublication(
                    "pub-mixed-feature", "svc-vts-mixed", "res-mixed-feature",
                    layerIndex: 0, publicationType: MetadataV2PublicationType.EsriFeatureLayer, isPrimary: true)
                .AddPublication(
                    "pub-mixed-vector", "svc-vts-mixed", "res-mixed-vector",
                    layerIndex: 1, publicationType: MetadataV2PublicationType.EsriVectorTileLayer)
                .AddResource("res-restricted-open", "restricted-open-layer", accessPolicy: openPolicy)
                .AddStorageBinding("binding-restricted-open", "res-restricted-open", "features", storageLayerId: RestrictedOpenStorageId)
                .AddResource("res-restricted-private", "restricted-private-layer", accessPolicy: privatePolicy)
                .AddStorageBinding("binding-restricted-private", "res-restricted-private", "features", storageLayerId: RestrictedPrivateStorageId)
                .AddService("svc-vts-restricted", "vts-restricted", protocols: [ServiceProtocols.VectorTileServer], accessPolicy: openPolicy)
                .AddPublication(
                    "pub-restricted-open", "svc-vts-restricted", "res-restricted-open",
                    layerIndex: 0, publicationType: MetadataV2PublicationType.EsriFeatureLayer, isPrimary: true)
                .AddPublication(
                    "pub-restricted-private", "svc-vts-restricted", "res-restricted-private",
                    layerIndex: 1, publicationType: MetadataV2PublicationType.EsriVectorTileLayer)
                .BuildProvider();
        }
    }
}
