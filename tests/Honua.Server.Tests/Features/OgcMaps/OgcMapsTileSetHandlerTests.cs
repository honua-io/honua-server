// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Security;
using Honua.Core.Features.Security.Abstractions;
using Honua.Server.Features.OgcMaps.Handlers;
using Honua.Server.Features.OgcMaps.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.OgcMaps;

/// <summary>
/// Tests for OgcMapsTileSetHandler functionality.
/// </summary>
[Protocol(Protocols.OgcApiMaps)]
public class OgcMapsTileSetHandlerTests
{
    private readonly ILayerCatalog _layerCatalog = Substitute.For<ILayerCatalog>();
    private readonly OgcMapsTileSetHandler _handler;

    public OgcMapsTileSetHandlerTests()
    {
        _handler = new OgcMapsTileSetHandler(
            _layerCatalog,
            NullLogger<OgcMapsTileSetHandler>.Instance);
    }

    [UnitTest]
    [Operation(Operations.GetTileMetadata)]
    public async Task GetMapTileSetsAsync_LayerNotFound_ReturnsNotFound()
    {
        _layerCatalog.GetLayerAsync(99, Arg.Any<CancellationToken>())
            .Returns((LayerDefinition?)null);

        var result = await _handler.GetMapTileSetsAsync(99);

        result.Should().BeOfType<NotFound>();
    }

    [UnitTest]
    [Operation(Operations.GetTileMetadata)]
    public async Task GetMapTileSetsAsync_ValidLayer_ReturnsOk()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());

        var result = await _handler.GetMapTileSetsAsync(1);

        result.Should().BeOfType<Ok<TileSet[]>>();
    }

    [UnitTest]
    [Operation(Operations.GetTileMetadata)]
    public async Task GetMapTileSetsAsync_ValidLayer_ReturnsTwoTileSets()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());

        var result = await _handler.GetMapTileSetsAsync(1);

        var okResult = result as Ok<TileSet[]>;
        okResult.Should().NotBeNull();
        okResult!.Value.Should().HaveCount(2);
    }

    [UnitTest]
    [Operation(Operations.GetTileMetadata)]
    public async Task GetMapTileSetsAsync_ValidLayer_IncludesWebMercator()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());

        var result = await _handler.GetMapTileSetsAsync(1);

        var okResult = result as Ok<TileSet[]>;
        okResult!.Value.Should().Contain(ts =>
            ts.Crs == "http://www.opengis.net/def/crs/EPSG/0/3857");
    }

    [UnitTest]
    [Operation(Operations.GetTileMetadata)]
    public async Task GetMapTileSetsAsync_ValidLayer_IncludesWgs84()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());

        var result = await _handler.GetMapTileSetsAsync(1);

        var okResult = result as Ok<TileSet[]>;
        okResult!.Value.Should().Contain(ts =>
            ts.Crs == "http://www.opengis.net/def/crs/OGC/1.3/CRS84");
    }

    [UnitTest]
    [Operation(Operations.GetTileMetadata)]
    public async Task GetMapTileSetsAsync_AccessDenied_ReturnsUnauthorized()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateRestrictedLayer());

        var context = CreateAnonymousOgcMapsContext();
        var result = await _handler.GetMapTileSetsAsync(1, context: context);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>();
        var statusCodeResult = (IStatusCodeHttpResult)result;
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    private static LayerDefinition CreateTestLayer()
        => LayerDefinition.CreateBasic(1, "test-layer", GeometryType.Point);

    private static LayerDefinition CreateRestrictedLayer()
        => CreateTestLayer() with
        {
            Metadata = new CatalogMetadata
            {
                AccessPolicy = new AccessPolicy
                {
                    AllowAnonymous = false
                }
            }
        };

    private static DefaultHttpContext CreateAnonymousOgcMapsContext()
    {
        var services = new ServiceCollection()
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>()
            .BuildServiceProvider();

        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity())
        };
        context.Request.Path = "/ogc/maps/collections/1/map/tiles";
        return context;
    }
}
