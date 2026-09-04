// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Tiles;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// End-to-end regression coverage for provider-routed GeoServices MVT and H3 surfaces.
/// The fallback services return distinct bytes/data so a silent primary-provider fallback
/// cannot satisfy these assertions.
/// </summary>
[Collection("Database")]
public sealed class ProviderRoutedTileAndH3EndpointTests :
    IClassFixture<ProviderRoutedTileAndH3EndpointTests.Fixture>
{
    private static readonly byte[] SecondaryMvt = [0x11, 0x12];
    private static readonly byte[] PrimaryMvt = [0x21, 0x22];
    private static readonly byte[] SecondaryH3Mvt = [0x31, 0x32];
    private static readonly byte[] FallbackMvt = [0x71, 0x72];
    private static readonly byte[] FallbackH3Mvt = [0x73, 0x74];

    private readonly Fixture _fixture;

    public ProviderRoutedTileAndH3EndpointTests(Fixture fixture) => _fixture = fixture;

    [IntegrationTest]
    [Protocol(TestProtocols.VectorTileServer)]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /rest/services/{serviceId}/VectorTileServer/tile/{z}/{y}/{x}.pbf")]
    public async Task VectorTileServer_RootStyleAndTile_UseSameRoutedPrimaryPublication()
    {
        var styleResponse = await _fixture.App.Client.GetAsync(
            "/rest/services/routed/VectorTileServer/resources/styles/root.json");
        styleResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var style = JsonDocument.Parse(await styleResponse.Content.ReadAsStringAsync());
        style.RootElement.GetProperty("name").GetString().Should().Be("primary-style");

        var tileResponse = await _fixture.App.Client.GetAsync(
            "/rest/services/routed/VectorTileServer/tile/1/0/0.pbf");
        tileResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await tileResponse.Content.ReadAsByteArrayAsync()).Should().Equal(PrimaryMvt);

        await _fixture.FallbackTileProvider.DidNotReceiveWithAnyArgs().GetMvtTileAsync(
            default, default, default, default, default, default!, default!, default, default);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /tiles/{layerId}/{z}/{x}/{y}.mvt")]
    public async Task FeatureServerMvt_SourceBackedLayer_UsesBindingScopedTileProvider()
    {
        var response = await _fixture.App.Client.GetAsync("/tiles/0/1/0/0.mvt");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(SecondaryMvt);
        await _fixture.SecondaryTileProvider.Received().GetMvtTileAsync(
            Arg.Is(41), Arg.Is(0), Arg.Is(0), Arg.Is(1), Arg.Any<FeatureQuery?>(), Arg.Any<TileOptions>(),
            Arg.Any<TileLimits>(), null, Arg.Any<CancellationToken>());
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.QueryH3)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryH3")]
    public async Task QueryH3_SourceBackedLayer_UsesBindingScopedFeatureReader()
    {
        var response = await _fixture.App.Client.GetAsync(
            "/rest/services/routed/FeatureServer/0/queryH3?resolution=5&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("routed-cell");
        content.Should().NotContain("fallback-cell");
        await _fixture.SecondaryReader.Received().QueryH3Async(
            41, Arg.Any<FeatureQuery>(), Arg.Any<H3AggregationQuery>(), Arg.Any<CancellationToken>());
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.QueryH3)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryH3")]
    public async Task QueryH3Post_SourceBackedLayer_UsesBindingScopedFeatureReader()
    {
        var response = await _fixture.App.Client.PostAsJsonAsync(
            "/rest/services/routed/FeatureServer/0/queryH3", new { resolution = 5, f = "json" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("routed-cell").And.NotContain("fallback-cell");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.QueryH3)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryH3")]
    public async Task QueryH3Summaries_SourceBackedLayer_UsesBindingScopedFeatureReader()
    {
        var response = await _fixture.App.Client.PostAsJsonAsync(
            "/rest/services/routed/FeatureServer/0/queryH3", new
            {
                resolution = 5,
                summaries = new[] { new { id = "featureCount", kind = "count" } },
                include = new { cells = true, totals = false },
                f = "json"
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("honua.spatial-aggregation.v1").And.Contain("routed-cell").And.NotContain("fallback-cell");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.QueryH3)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryH3")]
    public async Task QueryH3_UnsupportedBoundReader_ReturnsCapabilityError()
    {
        var response = await _fixture.App.Client.GetAsync(
            "/rest/services/routed/FeatureServer/0/queryH3?resolution=6&f=json");

        await response.AssertGeoServicesErrorAsync(501);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("provider-secret");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /tiles/{layerId}/h3/{z}/{x}/{y}.mvt")]
    public async Task H3Tile_SourceBackedLayer_UsesBindingScopedTileProvider()
    {
        var response = await _fixture.App.Client.GetAsync("/tiles/0/h3/1/0/0.mvt?resolution=5");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(SecondaryH3Mvt);
        await _fixture.SecondaryTileProvider.Received().GetH3MvtTileAsync(
            Arg.Is(41), Arg.Is(0), Arg.Is(0), Arg.Is(1), Arg.Is(5), Arg.Any<FeatureQuery?>(), Arg.Any<TileOptions>(),
            Arg.Any<TileLimits>(), Arg.Any<CancellationToken>());
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /tiles/{layerId}/h3/{z}/{x}/{y}.mvt")]
    public async Task H3Tile_EmptyRoutedTile_PreservesCacheHeaders()
    {
        var response = await _fixture.App.Client.GetAsync("/tiles/0/h3/1/1/1.mvt?resolution=5");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.Private.Should().BeTrue();
        response.Headers.CacheControl.MaxAge.Should().BeGreaterThan(TimeSpan.Zero);
        response.Headers.Vary.Should().Contain("Authorization");
    }

    public sealed class Fixture : IAsyncLifetime
    {
        private readonly Guid _connectionId = Guid.NewGuid();

        public WebAppFixture App { get; }
        public ITileProvider FallbackTileProvider { get; } = Substitute.For<ITileProvider>();
        public ITileProvider SecondaryTileProvider { get; } = Substitute.For<ITileProvider>();
        public ITileProvider PrimaryTileProvider { get; } = Substitute.For<ITileProvider>();
        public IFeatureReader SecondaryReader { get; } = Substitute.For<IFeatureReader>();

        public Fixture()
        {
            ConfigureTileProviders();

            var fallbackReader = Substitute.For<IFeatureReader>();
            fallbackReader.QueryH3Async(
                    Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<H3AggregationQuery>(), Arg.Any<CancellationToken>())
                .Returns(CreateH3Rows("fallback-cell"));

            var provider = Substitute.For<IFeatureDataProvider, IBindableFeatureDataProvider, IBindableTileProvider>();
            provider.ProviderName.Returns(DataProviderNames.Postgis);
            provider.Capabilities.Returns(FeatureProviderCapabilities.ReadWritePostgis);
            provider.Reader.Returns(SecondaryReader);
            ((IBindableFeatureDataProvider)provider).CreateReaderForBinding(Arg.Any<FeatureProviderBinding>())
                .Returns(SecondaryReader);
            ((IBindableTileProvider)provider).CreateTileProviderForBinding(Arg.Any<FeatureProviderBinding>())
                .Returns(call => ((FeatureProviderBinding)call[0]).StorageBinding.Metadata.Id switch
                {
                    "binding-primary" => PrimaryTileProvider,
                    _ => SecondaryTileProvider
                });

            SecondaryReader.QueryH3Async(
                    Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<H3AggregationQuery>(), Arg.Any<CancellationToken>())
                .Returns(CreateH3Rows("routed-cell"));
            SecondaryReader.QueryH3Async(
                    Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Is<H3AggregationQuery>(query => query.Resolution == 6), Arg.Any<CancellationToken>())
                .Returns(_ => throw new NotSupportedException("provider-secret"));

            var connectionRegistry = Substitute.For<ISecureConnectionRegistry>();
            connectionRegistry.GetConnectionAsync("routed-connection", Arg.Any<CancellationToken>())
                .Returns(CreateConnection(_connectionId));
            var router = new FeatureProviderQueryRouter(
                connectionRegistry,
                new FeatureDataProviderRegistry([provider]));

            var graphProvider = BuildGraphProvider();
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

            App = new WebAppFixture()
                .WithTestLicense(HonuaEdition.Pro)
                .ReplaceService<IH3CapabilityChecker>(new PrimaryH3UnavailableChecker())
                .ReplaceService<IFeatureReader>(fallbackReader)
                .ReplaceService<ITileProvider>(FallbackTileProvider)
                .ReplaceService<FeatureProviderQueryRouter>(router)
                .ReplaceService<IOgcStyleProjection>(styleProjection)
                .ConfigureServices(services =>
                {
                    services.RemoveAll<IMetadataV2GraphProvider>();
                    services.AddSingleton<IMetadataV2GraphProvider>(graphProvider);
                });
        }

        public Task InitializeAsync() => App.InitializeAsync();

        public Task DisposeAsync() => App.DisposeAsync();

        private void ConfigureTileProviders()
        {
            FallbackTileProvider.GetMvtTileAsync(
                    Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<FeatureQuery?>(),
                    Arg.Any<TileOptions>(), Arg.Any<TileLimits>(), Arg.Any<GridGeometry?>(), Arg.Any<CancellationToken>())
                .Returns(FallbackMvt);
            FallbackTileProvider.GetH3MvtTileAsync(
                    Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(),
                    Arg.Any<FeatureQuery?>(), Arg.Any<TileOptions>(), Arg.Any<TileLimits>(), Arg.Any<CancellationToken>())
                .Returns(FallbackH3Mvt);

            SecondaryTileProvider.GetMvtTileAsync(
                    Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<FeatureQuery?>(),
                    Arg.Any<TileOptions>(), Arg.Any<TileLimits>(), Arg.Any<GridGeometry?>(), Arg.Any<CancellationToken>())
                .Returns(SecondaryMvt);
            SecondaryTileProvider.GetH3MvtTileAsync(
                    Arg.Any<int>(), Arg.Is(0), Arg.Is(0), Arg.Is(1), Arg.Is(5), Arg.Any<FeatureQuery?>(),
                    Arg.Any<TileOptions>(), Arg.Any<TileLimits>(), Arg.Any<CancellationToken>())
                .Returns(SecondaryH3Mvt);
            SecondaryTileProvider.GetH3MvtTileAsync(
                    Arg.Any<int>(), Arg.Is(1), Arg.Is(1), Arg.Is(1), Arg.Is(5), Arg.Any<FeatureQuery?>(),
                    Arg.Any<TileOptions>(), Arg.Any<TileLimits>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<byte[]?>(null));

            PrimaryTileProvider.GetMvtTileAsync(
                    Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<FeatureQuery?>(),
                    Arg.Any<TileOptions>(), Arg.Any<TileLimits>(), Arg.Any<GridGeometry?>(), Arg.Any<CancellationToken>())
                .Returns(PrimaryMvt);
        }

        private static TestMetadataV2GraphProvider BuildGraphProvider()
        {
            var fields = new[]
            {
                new MetadataV2Field
                {
                    Name = "objectid",
                    Type = MetadataV2FieldType.BigInteger,
                    Nullable = false,
                    SemanticRoles = ["id.primary"]
                }
            };

            return new TestMetadataV2GraphBuilder()
                .AddConnection("routed-connection", "routed", provider: DataProviderNames.Postgis)
                .AddResource("resource-secondary", "secondary", fields: fields)
                .AddStorageBinding(
                    "binding-secondary", "resource-secondary", "public.secondary",
                    connectionId: "routed-connection", storageLayerId: 41)
                .AddResource("resource-primary", "primary", fields: fields)
                .AddStorageBinding(
                    "binding-primary", "resource-primary", "public.primary",
                    connectionId: "routed-connection", storageLayerId: 42)
                .AddService(
                    "service-routed", "routed",
                    protocols: [ServiceProtocols.FeatureServer, ServiceProtocols.VectorTileServer])
                .AddPublication(
                    "publication-secondary", "service-routed", "resource-secondary",
                    layerIndex: 0, storageBindingId: "binding-secondary",
                    publicationType: MetadataV2PublicationType.EsriVectorTileLayer)
                .AddPublication(
                    "publication-primary", "service-routed", "resource-primary",
                    layerIndex: 1, storageBindingId: "binding-primary",
                    publicationType: MetadataV2PublicationType.EsriVectorTileLayer, isPrimary: true)
                .AddPublication(
                    "publication-secondary-feature", "service-routed", "resource-secondary",
                    layerIndex: 0, storageBindingId: "binding-secondary",
                    publicationType: MetadataV2PublicationType.EsriFeatureLayer)
                .AddPublication(
                    "publication-primary-feature", "service-routed", "resource-primary",
                    layerIndex: 1, storageBindingId: "binding-primary",
                    publicationType: MetadataV2PublicationType.EsriFeatureLayer, isPrimary: true)
                .BuildProvider();
        }

        private static DataConnection CreateConnection(Guid connectionId) => new()
        {
            ConnectionId = connectionId,
            Name = "routed",
            Host = "provider.example.test",
            Port = 5432,
            DatabaseName = "spatial",
            Username = "honua",
            Provider = DataProviderNames.Postgis,
            SecretRef = "env:HONUA_TEST_PROVIDER",
            SecretType = "environment",
            CreatedBy = "test"
        };

        private static ImmutableArray<IReadOnlyDictionary<string, object?>> CreateH3Rows(string cellIndex)
            => ImmutableArray.Create<IReadOnlyDictionary<string, object?>>(
                new Dictionary<string, object?>
                {
                    ["cellIndex"] = cellIndex,
                    ["featureCount"] = 1L
                });

        private sealed class PrimaryH3UnavailableChecker : IH3CapabilityChecker
        {
            public Task<bool?> IsH3AvailableAsync(CancellationToken cancellationToken = default)
                => Task.FromResult<bool?>(false);
        }
    }
}
