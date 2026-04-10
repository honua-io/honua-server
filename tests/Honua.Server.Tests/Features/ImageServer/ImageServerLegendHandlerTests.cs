// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Server.Features.ImageServer.Handlers;
using Honua.Server.Features.ImageServer.Models;
using Honua.Server.Features.ImageServer.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.ImageServer;

/// <summary>
/// Tests for <see cref="ImageServerLegendHandler"/>. The handler builds a
/// 5-class equal-interval legend over the layer raster statistics.
/// </summary>
[Protocol(Protocols.ImageServer)]
public class ImageServerLegendHandlerTests
{
    private readonly ILayerCatalog _layerCatalog = Substitute.For<ILayerCatalog>();
    private readonly IRasterStore _rasterStore = Substitute.For<IRasterStore>();
    private readonly ImageServerLegendHandler _handler;

    public ImageServerLegendHandlerTests()
    {
        _handler = new ImageServerLegendHandler(
            _layerCatalog,
            _rasterStore,
            new ImageServerLegendSwatchBuilder(),
            NullLogger<ImageServerLegendHandler>.Instance);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task GetLegendAsync_LayerNotFound_ReturnsNotFound()
    {
        _layerCatalog.GetLayerAsync(99, Arg.Any<CancellationToken>())
            .Returns((LayerDefinition?)null);

        var context = CreateImageServerContext();
        var result = await _handler.GetLegendAsync(context, 99, CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task GetLegendAsync_NoRasters_ReturnsNotFound()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());
        _rasterStore.ListRastersAsync(default, default)
            .ReturnsForAnyArgs(Array.Empty<RasterInfo>());

        var context = CreateImageServerContext();
        var result = await _handler.GetLegendAsync(context, 1, CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task GetLegendAsync_NormalRange_ReturnsFiveSwatches()
    {
        SetupSuccessfulLegend(min: 0, max: 100);

        var context = CreateImageServerContext();
        var result = await _handler.GetLegendAsync(context, 1, CancellationToken.None);

        var jsonResult = result as JsonHttpResult<LegendResponse>;
        jsonResult.Should().NotBeNull();
        jsonResult!.Value!.Layers.Should().HaveCount(1);
        jsonResult.Value.Layers[0].Legend.Should().HaveCount(5);
        jsonResult.Value.Layers[0].LayerName.Should().Be("test-layer");
        jsonResult.Value.Layers[0].LayerType.Should().Be("Raster Layer");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task GetLegendAsync_NormalRange_SwatchesCoverContiguousRange()
    {
        SetupSuccessfulLegend(min: 0, max: 100);

        var context = CreateImageServerContext();
        var result = await _handler.GetLegendAsync(context, 1, CancellationToken.None);

        var jsonResult = result as JsonHttpResult<LegendResponse>;
        jsonResult.Should().NotBeNull();
        var legend = jsonResult!.Value!.Layers[0].Legend;

        // Equal interval over [0, 100] with 5 classes -> 0-20, 20-40, 40-60, 60-80, 80-100.
        legend[0].Values.Should().Equal(0, 20);
        legend[1].Values.Should().Equal(20, 40);
        legend[2].Values.Should().Equal(40, 60);
        legend[3].Values.Should().Equal(60, 80);
        legend[4].Values.Should().Equal(80, 100);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task GetLegendAsync_NormalRange_SwatchPngBytesAreNonEmpty()
    {
        SetupSuccessfulLegend(min: 0, max: 100);

        var context = CreateImageServerContext();
        var result = await _handler.GetLegendAsync(context, 1, CancellationToken.None);

        var jsonResult = result as JsonHttpResult<LegendResponse>;
        jsonResult.Should().NotBeNull();
        var legend = jsonResult!.Value!.Layers[0].Legend;

        foreach (var entry in legend)
        {
            entry.ImageData.Should().NotBeNullOrEmpty();
            entry.ContentType.Should().Be("image/png");
            entry.Width.Should().Be(20);
            entry.Height.Should().Be(20);
        }
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task GetLegendAsync_UniformRaster_CollapsesToSingleClass()
    {
        SetupSuccessfulLegend(min: 5, max: 5);

        var context = CreateImageServerContext();
        var result = await _handler.GetLegendAsync(context, 1, CancellationToken.None);

        var jsonResult = result as JsonHttpResult<LegendResponse>;
        jsonResult.Should().NotBeNull();
        jsonResult!.Value!.Layers[0].Legend.Should().HaveCount(1);
        jsonResult.Value.Layers[0].Legend[0].Values.Should().Equal(5, 5);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task GetLegendAsync_NoStatistics_DefaultsToZeroToTwoFiftyFive()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());
        _rasterStore.ListRastersAsync(default, default)
            .ReturnsForAnyArgs([CreateTestRasterInfo()]);
        _rasterStore.GetStatisticsAsync(1, 100, null, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<RasterStatistics>());

        var context = CreateImageServerContext();
        var result = await _handler.GetLegendAsync(context, 1, CancellationToken.None);

        var jsonResult = result as JsonHttpResult<LegendResponse>;
        jsonResult.Should().NotBeNull();
        var legend = jsonResult!.Value!.Layers[0].Legend;
        legend.Should().HaveCount(5);
        legend[0].Values![0].Should().Be(0);
        legend[4].Values![1].Should().Be(255);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task GetLegendAsync_RasterStoreThrows_ReturnsServerError()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());
        _rasterStore.ListRastersAsync(default, default)
            .ReturnsForAnyArgs(_ => Task.FromException<RasterInfo[]>(new InvalidOperationException("boom")));

        var context = CreateImageServerContext();
        var result = await _handler.GetLegendAsync(context, 1, CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
    }

    private void SetupSuccessfulLegend(double min, double max)
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());
        _rasterStore.ListRastersAsync(default, default)
            .ReturnsForAnyArgs([CreateTestRasterInfo()]);
        _rasterStore.GetStatisticsAsync(1, 100, null, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new RasterStatistics
                {
                    Band = 1,
                    MinValue = min,
                    MaxValue = max,
                    MeanValue = (min + max) / 2,
                    StandardDeviation = 1,
                    ValidPixelCount = 1024,
                    NoDataPixelCount = 0,
                }
            });
    }

    private static DefaultHttpContext CreateImageServerContext()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);

        var context = new DefaultHttpContext();
        context.RequestServices = services.BuildServiceProvider();
        context.Request.Path = "/rest/services/1/ImageServer/legend";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static LayerDefinition CreateTestLayer()
        => LayerDefinition.CreateBasic(1, "test-layer", GeometryType.Point);

    private static RasterInfo CreateTestRasterInfo() => new()
    {
        Id = 100,
        LayerId = 1,
        Name = "test-raster",
        Width = 32,
        Height = 32,
        BandCount = 1,
        PixelType = "8BUI",
        Srid = 4326,
        GeoTransform = [0, 1, 0, 0, 0, -1],
        Extent = new RasterExtent { XMin = -1, YMin = -1, XMax = 1, YMax = 1, Srid = 4326 },
        CreatedAt = DateTime.UtcNow,
    };
}
