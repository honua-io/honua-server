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
using Microsoft.AspNetCore.Http.HttpResults;
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

        var result = await _handler.GetServiceInfoAsync(99);

        result.Should().BeOfType<NotFound>();
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfoAsync_NoRasters_ReturnsNotFound()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());
        _rasterStore.ListRastersAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<RasterInfo>());

        var result = await _handler.GetServiceInfoAsync(1);

        result.Should().BeOfType<NotFound>();
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfoAsync_NullExtent_ReturnsProblem()
    {
        SetupLayerAndRasters();
        _rasterStore.GetStatisticsAsync(1, 100, null, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<RasterStatistics>());
        _rasterStore.GetExtentAsync(1, 100, Arg.Any<CancellationToken>())
            .Returns((RasterExtent?)null);

        var result = await _handler.GetServiceInfoAsync(1);

        result.Should().BeAssignableTo<ProblemHttpResult>();
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfoAsync_ValidRequest_ReturnsOk()
    {
        SetupSuccessfulMetadata();

        var result = await _handler.GetServiceInfoAsync(1);

        result.Should().BeOfType<Ok<ImageServerServiceInfo>>();
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfoAsync_ResponseContainsLayerName()
    {
        SetupSuccessfulMetadata();

        var result = await _handler.GetServiceInfoAsync(1);

        var okResult = result as Ok<ImageServerServiceInfo>;
        okResult.Should().NotBeNull();
        okResult!.Value!.Name.Should().Be("test-layer");
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfoAsync_ResponseContainsBandCount()
    {
        SetupSuccessfulMetadata();

        var result = await _handler.GetServiceInfoAsync(1);

        var okResult = result as Ok<ImageServerServiceInfo>;
        okResult.Should().NotBeNull();
        okResult!.Value!.BandCount.Should().Be(3);
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfoAsync_ResponseContainsExtent()
    {
        SetupSuccessfulMetadata();

        var result = await _handler.GetServiceInfoAsync(1);

        var okResult = result as Ok<ImageServerServiceInfo>;
        okResult.Should().NotBeNull();
        okResult!.Value!.Extent.XMin.Should().Be(-180);
        okResult.Value.Extent.YMax.Should().Be(90);
        okResult.Value.Extent.SpatialReference.Wkid.Should().Be(4326);
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

        var result = await _handler.GetServiceInfoAsync(1);

        var okResult = result as Ok<ImageServerServiceInfo>;
        okResult.Should().NotBeNull();
        okResult!.Value!.MinValues.Should().BeEmpty();
        okResult.Value.MaxValues.Should().BeEmpty();
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

        var result = await _handler.GetServiceInfoAsync(1);

        var okResult = result as Ok<ImageServerServiceInfo>;
        okResult.Should().NotBeNull();
        okResult!.Value!.MinValues.Should().Equal(0);
        okResult.Value.MaxValues.Should().Equal(0);
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfoAsync_PixelSizeCalculated()
    {
        SetupSuccessfulMetadata();

        var result = await _handler.GetServiceInfoAsync(1);

        var okResult = result as Ok<ImageServerServiceInfo>;
        okResult.Should().NotBeNull();
        // Extent width = 360, raster width = 1024, pixelSizeX = 360/1024
        okResult!.Value!.PixelSizeX.Should().BeApproximately(360.0 / 1024, 0.0001);
        // Extent height = 180, raster height = 1024, pixelSizeY = 180/1024
        okResult.Value.PixelSizeY.Should().BeApproximately(180.0 / 1024, 0.0001);
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfoAsync_RasterStoreThrows_ReturnsProblem()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());
        _rasterStore.ListRastersAsync(1, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Database error"));

        var result = await _handler.GetServiceInfoAsync(1);

        result.Should().BeAssignableTo<ProblemHttpResult>();
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

        var result = await _handler.GetServiceInfoAsync(1);

        var okResult = result as Ok<ImageServerServiceInfo>;
        okResult.Should().NotBeNull();
        okResult!.Value!.SpatialReference.Wkid.Should().Be(4326);
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
