// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Server.Features.ImageServer.Handlers;
using Honua.Server.Features.ImageServer.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Honua.Server.Tests.Features.ImageServer;

/// <summary>
/// Tests for ImageServerMetadataHandler functionality.
/// </summary>
[Protocol(Protocols.ImageServer)]
public class ImageServerMetadataHandlerTests
{
    private readonly ILayerCatalog _layerCatalog = Substitute.For<ILayerCatalog>();
    private readonly IRasterStore _rasterStore = Substitute.For<IRasterStore>();
    private readonly ImageServerMetadataHandler _handler;

    public ImageServerMetadataHandlerTests()
    {
        _handler = new ImageServerMetadataHandler(
            _layerCatalog,
            _rasterStore,
            NullLogger<ImageServerMetadataHandler>.Instance);
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfoAsync_LayerNotFound_ReturnsNotFound()
    {
        _layerCatalog.GetLayerAsync(99, Arg.Any<CancellationToken>())
            .Returns((LayerDefinition?)null);

        var context = CreateImageServerContext();
        var result = await _handler.GetServiceInfoAsync(context, 99);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfoAsync_NoRasters_ReturnsNotFound()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());
        _rasterStore.ListRastersAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<RasterInfo>());

        var context = CreateImageServerContext();
        var result = await _handler.GetServiceInfoAsync(context, 1);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfoAsync_NullExtent_ReturnsServerError()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());
        _rasterStore.ListRastersAsync(1, Arg.Any<CancellationToken>())
            .Returns([CreateTestRasterInfo() with { Extent = null }]);
        _rasterStore.GetStatisticsAsync(1, 100, null, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<RasterStatistics>());

        var context = CreateImageServerContext();
        var result = await _handler.GetServiceInfoAsync(context, 1);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().BeOneOf(
            StatusCodes.Status500InternalServerError,
            StatusCodes.Status404NotFound);
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfoAsync_ValidRequest_ReturnsOk()
    {
        SetupSuccessfulMetadata();

        var context = CreateImageServerContext();
        var result = await _handler.GetServiceInfoAsync(context, 1);

        result.Should().BeOfType<JsonHttpResult<ImageServerServiceInfo>>();
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfoAsync_ResponseContainsLayerName()
    {
        SetupSuccessfulMetadata();

        var context = CreateImageServerContext();
        var result = await _handler.GetServiceInfoAsync(context, 1);

        var jsonResult = result as JsonHttpResult<ImageServerServiceInfo>;
        jsonResult.Should().NotBeNull();
        jsonResult!.Value!.Name.Should().Be("test-layer");
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfoAsync_ResponseContainsBandCount()
    {
        SetupSuccessfulMetadata();

        var context = CreateImageServerContext();
        var result = await _handler.GetServiceInfoAsync(context, 1);

        var jsonResult = result as JsonHttpResult<ImageServerServiceInfo>;
        jsonResult.Should().NotBeNull();
        jsonResult!.Value!.BandCount.Should().Be(3);
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfoAsync_ResponseAdvertisesCachedTileContract()
    {
        SetupSuccessfulMetadata();

        var context = CreateImageServerContext();
        var result = await _handler.GetServiceInfoAsync(context, 1);

        var jsonResult = result as JsonHttpResult<ImageServerServiceInfo>;
        jsonResult.Should().NotBeNull();
        jsonResult!.Value!.Capabilities.Should().Contain("Tilemap");
        jsonResult.Value.SingleFusedMapCache.Should().BeTrue();
        jsonResult.Value.CacheType.Should().Be("Map");
        jsonResult.Value.TileInfo.Should().NotBeNull();
        jsonResult.Value.TileInfo!.Rows.Should().Be(256);
        jsonResult.Value.TileInfo.Cols.Should().Be(256);
        jsonResult.Value.TileInfo.Format.Should().Be("PNG");
        jsonResult.Value.TileInfo.SpatialReference.Wkid.Should().Be(3857);
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfoAsync_ResponseContainsExtent()
    {
        SetupSuccessfulMetadata();

        var context = CreateImageServerContext();
        var result = await _handler.GetServiceInfoAsync(context, 1);

        var jsonResult = result as JsonHttpResult<ImageServerServiceInfo>;
        jsonResult.Should().NotBeNull();
        jsonResult!.Value!.Extent.XMin.Should().Be(-180);
        jsonResult.Value.Extent.YMax.Should().Be(90);
        jsonResult.Value.Extent.SpatialReference.Wkid.Should().Be(4326);
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfoAsync_EmptyStatistics_ReturnsOk()
    {
        SetupLayerAndRasters();
        _rasterStore.GetStatisticsAsync(1, 100, null, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<RasterStatistics>());
        _rasterStore.GetExtentAsync(1, 100, Arg.Any<CancellationToken>())
            .Returns(CreateTestExtent());

        var context = CreateImageServerContext();
        var result = await _handler.GetServiceInfoAsync(context, 1);

        var jsonResult = result as JsonHttpResult<ImageServerServiceInfo>;
        jsonResult.Should().NotBeNull();
        jsonResult!.Value!.MinValues.Should().BeEmpty();
        jsonResult.Value.MaxValues.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfoAsync_NullStatisticValues_UsesDefaultZero()
    {
        SetupLayerAndRasters();
        _rasterStore.GetStatisticsAsync(1, 100, null, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new RasterStatistics { Band = 1, MinValue = null, MaxValue = null, MeanValue = null, StandardDeviation = null }
            });
        _rasterStore.GetExtentAsync(1, 100, Arg.Any<CancellationToken>())
            .Returns(CreateTestExtent());

        var context = CreateImageServerContext();
        var result = await _handler.GetServiceInfoAsync(context, 1);

        var jsonResult = result as JsonHttpResult<ImageServerServiceInfo>;
        jsonResult.Should().NotBeNull();
        jsonResult!.Value!.MinValues.Should().Equal(0);
        jsonResult.Value.MaxValues.Should().Equal(0);
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfoAsync_PixelSizeCalculated()
    {
        SetupSuccessfulMetadata();

        var context = CreateImageServerContext();
        var result = await _handler.GetServiceInfoAsync(context, 1);

        var jsonResult = result as JsonHttpResult<ImageServerServiceInfo>;
        jsonResult.Should().NotBeNull();
        jsonResult!.Value!.PixelSizeX.Should().BeApproximately(1.0, 0.0001);
        jsonResult.Value.PixelSizeY.Should().BeApproximately(1.0, 0.0001);
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfoAsync_RasterStoreThrows_ReturnsServerError()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());
        _rasterStore.ListRastersAsync(1, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Database error"));

        var context = CreateImageServerContext();
        var result = await _handler.GetServiceInfoAsync(context, 1);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfoAsync_NullSrid_DefaultsToWgs84()
    {
        SetupLayerAndRasters(srid: null);
        _rasterStore.GetStatisticsAsync(1, 100, null, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<RasterStatistics>());
        _rasterStore.GetExtentAsync(1, 100, Arg.Any<CancellationToken>())
            .Returns(new RasterExtent { XMin = 0, YMin = 0, XMax = 1, YMax = 1, Srid = null });

        var context = CreateImageServerContext();
        var result = await _handler.GetServiceInfoAsync(context, 1);

        var jsonResult = result as JsonHttpResult<ImageServerServiceInfo>;
        jsonResult.Should().NotBeNull();
        jsonResult!.Value!.SpatialReference.Wkid.Should().Be(4326);
    }

    private static DefaultHttpContext CreateImageServerContext()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);

        var context = new DefaultHttpContext();
        context.RequestServices = services.BuildServiceProvider();
        context.Request.Path = "/rest/services/1/ImageServer";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private void SetupLayerAndRasters(int? srid = 4326)
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());
        _rasterStore.ListRastersAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { CreateTestRasterInfo(srid: srid) });
    }

    private void SetupSuccessfulMetadata()
    {
        SetupLayerAndRasters();
        _rasterStore.GetStatisticsAsync(1, 100, null, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new RasterStatistics { Band = 1, MinValue = 0, MaxValue = 255, MeanValue = 128, StandardDeviation = 45 },
                new RasterStatistics { Band = 2, MinValue = 0, MaxValue = 255, MeanValue = 100, StandardDeviation = 50 },
                new RasterStatistics { Band = 3, MinValue = 0, MaxValue = 255, MeanValue = 80, StandardDeviation = 55 }
            });
        _rasterStore.GetExtentAsync(1, 100, Arg.Any<CancellationToken>())
            .Returns(CreateTestExtent());
    }

    private static LayerDefinition CreateTestLayer()
        => LayerDefinition.CreateBasic(1, "test-layer", GeometryType.Point);

    private static RasterInfo CreateTestRasterInfo(int? srid = 4326) => new()
    {
        Id = 100,
        LayerId = 1,
        Name = "test-raster",
        Width = 1024,
        Height = 1024,
        BandCount = 3,
        PixelType = "8BUI",
        Srid = srid,
        GeoTransform = [0, 1, 0, 0, 0, -1],
        Extent = new RasterExtent { XMin = -180, YMin = -90, XMax = 180, YMax = 90, Srid = 4326 },
        CreatedAt = DateTime.UtcNow
    };

    private static RasterExtent CreateTestExtent() => new()
    {
        XMin = -180,
        YMin = -90,
        XMax = 180,
        YMax = 90,
        Srid = 4326
    };
}
