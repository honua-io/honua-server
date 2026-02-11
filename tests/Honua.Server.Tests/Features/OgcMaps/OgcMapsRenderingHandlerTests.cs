// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
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
/// Tests for OgcMapsRenderingHandler functionality.
/// </summary>
[Protocol(Protocols.OgcApiMaps)]
public class OgcMapsRenderingHandlerTests
{
    private readonly ILayerCatalog _layerCatalog = Substitute.For<ILayerCatalog>();
    private readonly IRasterMapRenderer _mapRenderer = Substitute.For<IRasterMapRenderer>();
    private readonly OgcMapsRenderingHandler _handler;

    public OgcMapsRenderingHandlerTests()
    {
        _handler = new OgcMapsRenderingHandler(
            _layerCatalog,
            _mapRenderer,
            NullLogger<OgcMapsRenderingHandler>.Instance);
    }

    // =========================================================================
    // RenderCollectionMapAsync
    // =========================================================================

    [UnitTest]
    [Operation(Operations.Render)]
    public async Task RenderCollectionMapAsync_LayerNotFound_ReturnsNotFound()
    {
        _layerCatalog.GetLayerAsync(99, Arg.Any<CancellationToken>())
            .Returns((LayerDefinition?)null);

        var result = await _handler.RenderCollectionMapAsync(99, CreateDefaultRequest());

        result.Should().BeOfType<NotFound>();
    }

    [UnitTest]
    [Operation(Operations.Render)]
    public async Task RenderCollectionMapAsync_InvalidBbox_ReturnsBadRequest()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());

        var request = new OgcMapRequest { Bbox = "invalid" };
        var result = await _handler.RenderCollectionMapAsync(1, request);

        result.Should().BeOfType<BadRequest<string>>();
    }

    [UnitTest]
    [Operation(Operations.Render)]
    public async Task RenderCollectionMapAsync_UnsupportedFormat_ReturnsBadRequest()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());

        var request = new OgcMapRequest
        {
            Bbox = "-180,-90,180,90",
            F = "json"
        };
        var result = await _handler.RenderCollectionMapAsync(1, request);

        result.Should().BeOfType<BadRequest<string>>();
    }

    [UnitTest]
    [Operation(Operations.Render)]
    public async Task RenderCollectionMapAsync_DimensionsExceedMax_ReturnsBadRequest()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());

        var request = new OgcMapRequest
        {
            Bbox = "-180,-90,180,90",
            Width = 5000,
            Height = 256
        };
        var result = await _handler.RenderCollectionMapAsync(1, request);

        result.Should().BeOfType<BadRequest<string>>();
    }

    [UnitTest]
    [Operation(Operations.Render)]
    public async Task RenderCollectionMapAsync_ZeroDimensions_ReturnsBadRequest()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());

        var request = new OgcMapRequest
        {
            Bbox = "-180,-90,180,90",
            Width = 0,
            Height = 256
        };
        var result = await _handler.RenderCollectionMapAsync(1, request);

        result.Should().BeOfType<BadRequest<string>>();
    }

    [UnitTest]
    [Operation(Operations.Render)]
    public async Task RenderCollectionMapAsync_ValidRequest_ReturnsFileResult()
    {
        var tileData = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());
        _mapRenderer.RenderCollectionMapAsync(1, Arg.Any<MapRenderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RasterResult
            {
                Data = tileData,
                ContentType = "image/png",
                Width = 256,
                Height = 256,
                Srid = 3857
            });

        var request = new OgcMapRequest { Bbox = "-180,-90,180,90" };
        var result = await _handler.RenderCollectionMapAsync(1, request);

        result.Should().BeOfType<FileContentHttpResult>();
    }

    [UnitTest]
    [Operation(Operations.Render)]
    public async Task RenderCollectionMapAsync_EmptyRasterResult_ReturnsNotFound()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());
        _mapRenderer.RenderCollectionMapAsync(1, Arg.Any<MapRenderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RasterResult
            {
                Data = Array.Empty<byte>(),
                ContentType = "image/png",
                Width = 256,
                Height = 256,
                Srid = 4326
            });

        var request = new OgcMapRequest { Bbox = "-180,-90,180,90" };
        var result = await _handler.RenderCollectionMapAsync(1, request);

        result.Should().BeOfType<NotFound>();
    }

    [UnitTest]
    [Operation(Operations.Render)]
    public async Task RenderCollectionMapAsync_NoBbox_UsesLayerExtent()
    {
        var tileData = new byte[] { 0x89 };
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayerWithExtent());
        _mapRenderer.RenderCollectionMapAsync(1, Arg.Any<MapRenderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RasterResult
            {
                Data = tileData,
                ContentType = "image/png",
                Width = 256,
                Height = 256,
                Srid = 4326
            });

        // Request without bbox - should fall back to layer extent
        var request = new OgcMapRequest();
        var result = await _handler.RenderCollectionMapAsync(1, request);

        result.Should().BeOfType<FileContentHttpResult>();
    }

    [UnitTest]
    [Operation(Operations.Render)]
    public async Task RenderCollectionMapAsync_NoBboxNoExtent_ReturnsBadRequest()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer()); // No extent set

        var request = new OgcMapRequest { Bbox = null };
        var result = await _handler.RenderCollectionMapAsync(1, request);

        result.Should().BeOfType<BadRequest<string>>();
    }

    [UnitTest]
    [Operation(Operations.Render)]
    public async Task RenderCollectionMapAsync_AccessDenied_ReturnsUnauthorized()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateRestrictedLayer());

        var context = CreateAnonymousOgcMapsContext();
        var result = await _handler.RenderCollectionMapAsync(1, CreateDefaultRequest(), context: context);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>();
        var statusCodeResult = (IStatusCodeHttpResult)result;
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        await _mapRenderer.DidNotReceive().RenderCollectionMapAsync(
            Arg.Any<int>(),
            Arg.Any<MapRenderRequest>(),
            Arg.Any<CancellationToken>());
    }

    // =========================================================================
    // RenderDatasetMapAsync
    // =========================================================================

    [UnitTest]
    [Operation(Operations.Render)]
    public async Task RenderDatasetMapAsync_LayerNotFound_ReturnsNotFound()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());
        _layerCatalog.GetLayerAsync(99, Arg.Any<CancellationToken>())
            .Returns((LayerDefinition?)null);

        var result = await _handler.RenderDatasetMapAsync([1, 99], CreateDefaultRequest());

        result.Should().BeOfType<NotFound>();
    }

    [UnitTest]
    [Operation(Operations.Render)]
    public async Task RenderDatasetMapAsync_ValidLayers_ReturnsFileResult()
    {
        var tileData = new byte[] { 0x89, 0x50 };
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());
        _layerCatalog.GetLayerAsync(2, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer(2));
        _mapRenderer.RenderDatasetMapAsync(Arg.Any<int[]>(), Arg.Any<MapRenderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RasterResult
            {
                Data = tileData,
                ContentType = "image/png",
                Width = 256,
                Height = 256,
                Srid = 3857
            });

        var result = await _handler.RenderDatasetMapAsync([1, 2], CreateDefaultRequest());

        result.Should().BeOfType<FileContentHttpResult>();
    }

    [UnitTest]
    [Operation(Operations.Render)]
    public async Task RenderDatasetMapAsync_EmptyRasterResult_ReturnsNotFound()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());
        _layerCatalog.GetLayerAsync(2, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer(2));
        _mapRenderer.RenderDatasetMapAsync(Arg.Any<int[]>(), Arg.Any<MapRenderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RasterResult
            {
                Data = Array.Empty<byte>(),
                ContentType = "image/png",
                Width = 256,
                Height = 256,
                Srid = 4326
            });

        var result = await _handler.RenderDatasetMapAsync([1, 2], CreateDefaultRequest());

        result.Should().BeOfType<NotFound>();
    }

    // =========================================================================
    // RenderStyledMapAsync
    // =========================================================================

    [UnitTest]
    [Operation(Operations.Render)]
    public async Task RenderStyledMapAsync_LayerNotFound_ReturnsNotFound()
    {
        _layerCatalog.GetLayerAsync(99, Arg.Any<CancellationToken>())
            .Returns((LayerDefinition?)null);

        var result = await _handler.RenderStyledMapAsync(99, "default", CreateDefaultRequest());

        result.Should().BeOfType<NotFound>();
    }

    [UnitTest]
    [Operation(Operations.Render)]
    public async Task RenderStyledMapAsync_ValidRequest_ReturnsFileResult()
    {
        var tileData = new byte[] { 0xFF, 0xD8 };
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());
        _mapRenderer.RenderStyledMapAsync(1, "dark", Arg.Any<MapRenderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RasterResult
            {
                Data = tileData,
                ContentType = "image/jpeg",
                Width = 512,
                Height = 512,
                Srid = 3857
            });

        var request = new OgcMapRequest
        {
            Bbox = "-180,-90,180,90",
            Width = 512,
            Height = 512,
            F = "jpeg"
        };
        var result = await _handler.RenderStyledMapAsync(1, "dark", request);

        result.Should().BeOfType<FileContentHttpResult>();
    }

    [UnitTest]
    [Operation(Operations.Render)]
    public async Task RenderStyledMapAsync_UnsupportedRenderer_ReturnsNotImplemented()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());
        _mapRenderer.RenderStyledMapAsync(1, "default", Arg.Any<MapRenderRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<RasterResult>(new NotSupportedException("Styles are not supported")));

        var result = await _handler.RenderStyledMapAsync(1, "default", CreateDefaultRequest());

        result.Should().BeAssignableTo<IStatusCodeHttpResult>();
        var statusCodeResult = (IStatusCodeHttpResult)result;
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status501NotImplemented);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static OgcMapRequest CreateDefaultRequest() => new()
    {
        Bbox = "-180,-90,180,90",
        Width = 256,
        Height = 256,
        F = "png"
    };

    private static LayerDefinition CreateTestLayer(int id = 1)
        => LayerDefinition.CreateBasic(id, $"test-layer-{id}", GeometryType.Point);

    private static LayerDefinition CreateRestrictedLayer(int id = 1)
        => CreateTestLayer(id) with
        {
            Metadata = new CatalogMetadata
            {
                AccessPolicy = new AccessPolicy
                {
                    AllowAnonymous = false
                }
            }
        };

    private static LayerDefinition CreateTestLayerWithExtent(int id = 1)
        => CreateTestLayer(id) with
        {
            Extent = new FeatureExtent
            {
                MinX = -180,
                MinY = -90,
                MaxX = 180,
                MaxY = 90,
                SpatialReference = 4326
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
        context.Request.Path = "/ogc/maps/collections/1/map";
        return context;
    }
}
