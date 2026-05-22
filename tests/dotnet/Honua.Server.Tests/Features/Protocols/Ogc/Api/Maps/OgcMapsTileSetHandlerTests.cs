// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Server.Features.Protocols.Ogc.Api.Maps.Handlers;
using Honua.Server.Features.Protocols.Ogc.Api.Maps.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Maps;

/// <summary>
/// Tests for <see cref="OgcMapsTileSetHandler"/> after the Metadata v2 cutover
/// (#1035 cutover 86/N). The handler now resolves the V2 resource by the
/// storage-layer-id integer (via <see cref="MetadataV2GraphIndex.ResourcesByStorageLayerId"/>)
/// and reads protocol membership / access policy from the matching V2 service.
/// </summary>
[Protocol(TestProtocols.OgcApiMaps)]
public class OgcMapsTileSetHandlerTests
{
    private const string OgcApiMapsProtocol = "OGC-API-Maps";
    private const string OgcApiTilesProtocol = "OGC-API-Tiles";

    [UnitTest]
    [Operation(Operations.GetTileMetadata)]
    public async Task GetMapTileSetsAsync_LayerNotFound_ReturnsNotFound()
    {
        var handler = CreateHandler(BuildEmptyProvider());

        var result = await handler.GetMapTileSetsAsync(99);

        result.Should().BeOfType<NotFound>();
    }

    [UnitTest]
    [Operation(Operations.GetTileMetadata)]
    public async Task GetMapTileSetsAsync_ProtocolDisabled_ReturnsNotFound()
    {
        var provider = BuildProvider(layerId: 1, mapsEnabled: false, allowAnonymous: true);
        var handler = CreateHandler(provider);

        var result = await handler.GetMapTileSetsAsync(1, context: CreateOgcMapsContext());

        result.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [UnitTest]
    [Operation(Operations.GetTileMetadata)]
    public async Task GetMapTileSetsAsync_MultiServiceLayer_PrefersMapsProtocolEnabledService()
    {
        // Two services both publish the same resource; only one declares OGC API Maps.
        // The handler must pick the maps-enabled one.
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("resource-1", "test-layer", MetadataV2ResourceType.FeatureDataset, accessPolicy: PublicPolicy())
            .AddStorageBinding("binding-1", "resource-1", "test", storageLayerId: 1)
            .AddService("alpha-service", "alpha-service", protocols: [OgcApiTilesProtocol])
            .AddService("beta-service", "beta-service", protocols: [OgcApiMapsProtocol, OgcApiTilesProtocol])
            .AddPublication("alpha-pub", "alpha-service", "resource-1", layerIndex: 1)
            .AddPublication("beta-pub", "beta-service", "resource-1", layerIndex: 1)
            .Build();
        var handler = CreateHandler(new TestMetadataV2GraphProvider(graph));

        var result = await handler.GetMapTileSetsAsync(1, context: CreateOgcMapsContext());

        result.Should().BeOfType<Ok<TileSetsList>>();
    }

    [UnitTest]
    [Operation(Operations.GetTileMetadata)]
    public async Task GetMapTileSetsAsync_ValidLayer_ReturnsOk()
    {
        var handler = CreateHandler(BuildProvider(layerId: 1));

        var result = await handler.GetMapTileSetsAsync(1);

        result.Should().BeOfType<Ok<TileSetsList>>();
    }

    [UnitTest]
    [Operation(Operations.GetTileMetadata)]
    public async Task GetMapTileSetsAsync_ValidLayer_ReturnsTwoTileSets()
    {
        var handler = CreateHandler(BuildProvider(layerId: 1));

        var result = await handler.GetMapTileSetsAsync(1);

        var okResult = result as Ok<TileSetsList>;
        okResult.Should().NotBeNull();
        okResult!.Value!.Tilesets.Should().HaveCount(2);
    }

    [UnitTest]
    [Operation(Operations.GetTileMetadata)]
    public async Task GetMapTileSetsAsync_ValidLayer_IncludesWebMercator()
    {
        var handler = CreateHandler(BuildProvider(layerId: 1));

        var result = await handler.GetMapTileSetsAsync(1);

        var okResult = result as Ok<TileSetsList>;
        okResult!.Value!.Tilesets.Should().Contain(ts =>
            ts.Crs == "http://www.opengis.net/def/crs/EPSG/0/3857");
    }

    [UnitTest]
    [Operation(Operations.GetTileMetadata)]
    public async Task GetMapTileSetsAsync_ValidLayer_IncludesWgs84()
    {
        var handler = CreateHandler(BuildProvider(layerId: 1));

        var result = await handler.GetMapTileSetsAsync(1);

        var okResult = result as Ok<TileSetsList>;
        okResult!.Value!.Tilesets.Should().Contain(ts =>
            ts.Crs == "http://www.opengis.net/def/crs/OGC/1.3/CRS84");
    }

    [UnitTest]
    [Operation(Operations.GetTileMetadata)]
    public async Task GetMapTileSetsAsync_ValidLayer_IncludesTileMatrixSetId()
    {
        var handler = CreateHandler(BuildProvider(layerId: 1));

        var result = await handler.GetMapTileSetsAsync(1);

        var okResult = result as Ok<TileSetsList>;
        okResult!.Value!.Tilesets.Should().Contain(ts => ts.TileMatrixSetId == "WebMercatorQuad");
        okResult!.Value!.Tilesets.Should().Contain(ts => ts.TileMatrixSetId == "WorldCRS84Quad");
    }

    [UnitTest]
    [Operation(Operations.GetTileMetadata)]
    public async Task GetMapTileSetsAsync_WithContext_LinksAreAbsolute()
    {
        var handler = CreateHandler(BuildProvider(layerId: 1));

        var context = CreateOgcMapsContext();
        var result = await handler.GetMapTileSetsAsync(1, context: context);

        var okResult = result as Ok<TileSetsList>;
        okResult.Should().NotBeNull();
        okResult!.Value!.Links.Should().OnlyContain(link => link.Href.StartsWith("http"));
        foreach (var tileSet in okResult.Value.Tilesets)
        {
            foreach (var link in tileSet.Links)
            {
                link.Href.Should().StartWith("http", "tile set links should be absolute URIs");
            }
        }
    }

    [UnitTest]
    [Operation(Operations.GetTileMetadata)]
    public async Task GetMapTileSetsAsync_WithoutConfiguredBaseUrl_UsesSafeAbsoluteLinks()
    {
        var handler = CreateHandler(BuildProvider(layerId: 1, allowAnonymous: true));

        var context = CreateAnonymousOgcMapsContext();
        var result = await handler.GetMapTileSetsAsync(1, context: context);

        var okResult = result as Ok<TileSetsList>;
        okResult.Should().NotBeNull();
        okResult!.Value!.Links.Should().OnlyContain(link => link.Href.StartsWith("http://localhost", StringComparison.Ordinal));
        foreach (var tileSet in okResult.Value.Tilesets)
        {
            foreach (var link in tileSet.Links)
            {
                link.Href.Should().StartWith("http://localhost", "tile set links should use the shared safe-origin fallback");
            }
        }
    }

    [UnitTest]
    [Operation(Operations.GetTileMetadata)]
    public async Task GetMapTileSetsAsync_TileSets_IncludeTilingSchemeLinks()
    {
        var handler = CreateHandler(BuildProvider(layerId: 1));

        var context = CreateOgcMapsContext();
        var result = await handler.GetMapTileSetsAsync(1, context: context);

        var okResult = result as Ok<TileSetsList>;
        okResult.Should().NotBeNull();
        foreach (var tileSet in okResult!.Value!.Tilesets)
        {
            tileSet.Links.Should().Contain(link =>
                link.Rel == "http://www.opengis.net/def/rel/ogc/1.0/tiling-scheme",
                "each tileset should include a tiling-scheme link");
        }
    }

    [UnitTest]
    [Operation(Operations.GetTileMetadata)]
    public async Task GetMapTileSetAsync_ValidLayer_ReturnsIndividualTileset()
    {
        var handler = CreateHandler(BuildProvider(layerId: 1));

        var result = await handler.GetMapTileSetAsync(1, "WebMercatorQuad");

        var okResult = result as Ok<TileSet>;
        okResult.Should().NotBeNull();
        okResult!.Value!.TileMatrixSetId.Should().Be("WebMercatorQuad");
        okResult.Value.Links.Should().Contain(link => link.Rel == "self");
        okResult.Value.Links.Should().Contain(link => link.Rel == "item");
    }

    [UnitTest]
    [Operation(Operations.GetTileMetadata)]
    public async Task GetMapTileSetAsync_ValidLayer_ItemLinkIsMarkedTemplated()
    {
        var handler = CreateHandler(BuildProvider(layerId: 1));

        var result = await handler.GetMapTileSetAsync(1, "WebMercatorQuad");

        var okResult = result as Ok<TileSet>;
        okResult.Should().NotBeNull();
        okResult!.Value!.Links.Should().ContainSingle(link =>
            link.Rel == "item" &&
            link.Templated == true);
    }

    [UnitTest]
    [Operation(Operations.GetTileMetadata)]
    public async Task GetMapTileSetAsync_WhenOgcTilesDisabled_OmitsTileItemLinks()
    {
        // Maps enabled but Tiles disabled on the resolved publication.
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("resource-1", "test-layer", MetadataV2ResourceType.FeatureDataset, accessPolicy: PublicPolicy())
            .AddStorageBinding("binding-1", "resource-1", "test", storageLayerId: 1)
            .AddService("maps-only-service", "maps-only-service", protocols: [OgcApiMapsProtocol])
            .AddPublication("pub-1", "maps-only-service", "resource-1", layerIndex: 1)
            .Build();
        var handler = CreateHandler(new TestMetadataV2GraphProvider(graph));

        var result = await handler.GetMapTileSetAsync(1, "WebMercatorQuad", context: CreateOgcMapsContext());

        var okResult = result as Ok<TileSet>;
        okResult.Should().NotBeNull();
        okResult!.Value!.Links.Should().NotContain(link => link.Rel == "item");
        okResult.Value.Links.Should().NotContain(link =>
            link.Href.Contains("/ogc/tiles/", StringComparison.OrdinalIgnoreCase));
    }

    [UnitTest]
    [Operation(Operations.GetTileMetadata)]
    public async Task GetMapTileSetsAsync_AccessDenied_ReturnsUnauthorized()
    {
        var handler = CreateHandler(BuildProvider(layerId: 1, allowAnonymous: false));

        var context = CreateAnonymousOgcMapsContext();
        var result = await handler.GetMapTileSetsAsync(1, context: context);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>();
        var statusCodeResult = (IStatusCodeHttpResult)result;
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [UnitTest]
    [Operation(Operations.GetTileMetadata)]
    public async Task GetMapTileSetsAsync_ServiceRestrictionOverridesPublicLayerAccess()
    {
        // Resource is public, but the OGC API Maps service requires a role.
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("resource-1", "test-layer", MetadataV2ResourceType.FeatureDataset, accessPolicy: PublicPolicy())
            .AddStorageBinding("binding-1", "resource-1", "test", storageLayerId: 1)
            .AddService("restricted-service", "restricted-service",
                protocols: [OgcApiMapsProtocol],
                accessPolicy: new AccessPolicy { AllowedRoles = ["service-reader"] })
            .AddPublication("pub-1", "restricted-service", "resource-1", layerIndex: 1)
            .Build();
        var handler = CreateHandler(new TestMetadataV2GraphProvider(graph));

        var context = CreateAnonymousOgcMapsContext();
        var result = await handler.GetMapTileSetsAsync(1, context: context);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>();
        var statusCodeResult = (IStatusCodeHttpResult)result;
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    private static OgcMapsTileSetHandler CreateHandler(TestMetadataV2GraphProvider provider)
        => new(provider, NullLogger<OgcMapsTileSetHandler>.Instance);

    private static TestMetadataV2GraphProvider BuildEmptyProvider()
        => new TestMetadataV2GraphBuilder().BuildProvider();

    private static TestMetadataV2GraphProvider BuildProvider(
        int layerId,
        bool mapsEnabled = true,
        bool? allowAnonymous = null)
    {
        var protocols = mapsEnabled
            ? new[] { OgcApiMapsProtocol, OgcApiTilesProtocol }
            : new[] { OgcApiTilesProtocol };
        var policy = allowAnonymous switch
        {
            true => PublicPolicy(),
            false => new AccessPolicy { AllowAnonymous = false },
            _ => null,
        };
        return new TestMetadataV2GraphBuilder()
            .AddResource($"resource-{layerId}", "test-layer", MetadataV2ResourceType.FeatureDataset, accessPolicy: policy)
            .AddStorageBinding($"binding-{layerId}", $"resource-{layerId}", "test", storageLayerId: layerId)
            .AddService($"service-{layerId}", $"service-{layerId}", protocols: protocols)
            .AddPublication($"pub-{layerId}", $"service-{layerId}", $"resource-{layerId}", layerIndex: layerId)
            .BuildProvider();
    }

    private static AccessPolicy PublicPolicy() => new() { AllowAnonymous = true };

    private static DefaultHttpContext CreateOgcMapsContext()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Public:BaseUrl"] = "https://api.example.test"
            })
            .Build();

        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>()
            .BuildServiceProvider();

        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity("TestAuth"))
        };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("example.com");
        context.Request.Path = "/ogc/maps/collections/1/map/tiles";
        return context;
    }

    private static DefaultHttpContext CreateAnonymousOgcMapsContext()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>()
            .BuildServiceProvider();

        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity())
        };
        context.Request.Scheme = "http";
        context.Request.Path = "/ogc/maps/collections/1/map/tiles";
        return context;
    }
}
