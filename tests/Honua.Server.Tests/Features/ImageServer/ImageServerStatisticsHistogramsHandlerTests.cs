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
using Microsoft.Extensions.Primitives;
using NSubstitute;

namespace Honua.Server.Tests.Features.ImageServer;

/// <summary>
/// Tests for <see cref="ImageServerStatisticsHistogramsHandler"/>. Verifies the
/// per-band statistics and histograms returned by the
/// <c>computeStatisticsHistograms</c> endpoint.
/// </summary>
[Protocol(Protocols.ImageServer)]
public class ImageServerStatisticsHistogramsHandlerTests
{
    private readonly ILayerCatalog _layerCatalog = Substitute.For<ILayerCatalog>();
    private readonly IRasterStore _rasterStore = Substitute.For<IRasterStore>();
    private readonly ImageServerStatisticsHistogramsHandler _handler;

    public ImageServerStatisticsHistogramsHandlerTests()
    {
        _handler = new ImageServerStatisticsHistogramsHandler(
            _layerCatalog,
            _rasterStore,
            NullLogger<ImageServerStatisticsHistogramsHandler>.Instance);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ComputeAsync_LayerNotFound_ReturnsNotFound()
    {
        _layerCatalog.GetLayerAsync(99, Arg.Any<CancellationToken>())
            .Returns((LayerDefinition?)null);

        var context = CreateImageServerContext();
        var result = await _handler.ComputeAsync(context, 99, EmptyValues(), CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ComputeAsync_NoPrimaryRaster_ReturnsNotFound()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());
        _rasterStore.GetPrimaryRasterInfoAsync(1, Arg.Any<CancellationToken>())
            .Returns((RasterInfo?)null);

        var context = CreateImageServerContext();
        var result = await _handler.ComputeAsync(context, 1, EmptyValues(), CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ComputeAsync_UnsupportedFormat_ReturnsBadRequest()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());

        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["f"] = "html",
        };

        var context = CreateImageServerContext();
        var result = await _handler.ComputeAsync(context, 1, values, CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ComputeAsync_ValidRequest_ReturnsStatisticsAndHistograms()
    {
        SetupSuccessfulCompute();

        var context = CreateImageServerContext();
        var result = await _handler.ComputeAsync(context, 1, EmptyValues(), CancellationToken.None);

        var jsonResult = result as JsonHttpResult<ComputeStatisticsHistogramsResponse>;
        jsonResult.Should().NotBeNull();
        jsonResult!.Value!.Statistics.Should().HaveCount(1);
        jsonResult.Value.Statistics[0].Min.Should().Be(0);
        jsonResult.Value.Statistics[0].Max.Should().Be(255);
        jsonResult.Value.Statistics[0].Mean.Should().Be(128);
        jsonResult.Value.Statistics[0].StandardDeviation.Should().Be(45);
        jsonResult.Value.Statistics[0].Count.Should().Be(1024);
        jsonResult.Value.Histograms.Should().HaveCount(1);
        jsonResult.Value.Histograms[0].Size.Should().Be(4);
        jsonResult.Value.Histograms[0].Min.Should().Be(0);
        jsonResult.Value.Histograms[0].Max.Should().Be(255);
        jsonResult.Value.Histograms[0].Counts.Should().Equal(10L, 20L, 30L, 40L);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ComputeAsync_NullStatistics_DefaultToZero()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());
        _rasterStore.GetPrimaryRasterInfoAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestRasterInfo());
        _rasterStore.GetStatisticsAsync(1, 100, Arg.Any<int[]?>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new RasterStatistics { Band = 1, MinValue = null, MaxValue = null, MeanValue = null, StandardDeviation = null }
            });
        _rasterStore.GetHistogramsAsync(1, 100, Arg.Any<int[]?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<RasterHistogram>());

        var context = CreateImageServerContext();
        var result = await _handler.ComputeAsync(context, 1, EmptyValues(), CancellationToken.None);

        var jsonResult = result as JsonHttpResult<ComputeStatisticsHistogramsResponse>;
        jsonResult.Should().NotBeNull();
        jsonResult!.Value!.Statistics[0].Min.Should().Be(0);
        jsonResult.Value.Statistics[0].Max.Should().Be(0);
        jsonResult.Value.Statistics[0].Mean.Should().Be(0);
        jsonResult.Value.Statistics[0].StandardDeviation.Should().Be(0);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ComputeAsync_HistogramParametersWithSize_PassesBinCountToStore()
    {
        SetupSuccessfulCompute();

        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["histogramParameters"] = "{\"size\":64}",
        };

        var context = CreateImageServerContext();
        var result = await _handler.ComputeAsync(context, 1, values, CancellationToken.None);

        result.Should().BeOfType<JsonHttpResult<ComputeStatisticsHistogramsResponse>>();
        await _rasterStore.Received(1).GetHistogramsAsync(
            1,
            100,
            Arg.Any<int[]?>(),
            64,
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ComputeAsync_HistogramParametersClampedAtMax()
    {
        SetupSuccessfulCompute();

        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["histogramParameters"] = "{\"size\":4096}",
        };

        var context = CreateImageServerContext();
        var result = await _handler.ComputeAsync(context, 1, values, CancellationToken.None);

        result.Should().BeOfType<JsonHttpResult<ComputeStatisticsHistogramsResponse>>();
        await _rasterStore.Received(1).GetHistogramsAsync(
            1,
            100,
            Arg.Any<int[]?>(),
            1024,
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ComputeAsync_HistogramParametersInvalidJson_ReturnsBadRequest()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());

        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["histogramParameters"] = "{not-json",
        };

        var context = CreateImageServerContext();
        var result = await _handler.ComputeAsync(context, 1, values, CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ComputeAsync_HistogramParametersNegativeSize_ReturnsBadRequest()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());

        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["histogramParameters"] = "{\"size\":-5}",
        };

        var context = CreateImageServerContext();
        var result = await _handler.ComputeAsync(context, 1, values, CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ComputeAsync_RasterIdsCsv_LooksUpCatalogRastersAndComputesStatistics()
    {
        SetupSuccessfulCompute();
        // rasterIds in Esri spec are catalog object IDs - not band indices.
        var rasterA = CreateTestRasterInfo() with { Id = 100 };
        var rasterB = CreateTestRasterInfo() with { Id = 200 };
        _rasterStore.GetRasterInfoAsync(1, 100, Arg.Any<CancellationToken>()).Returns(rasterA);
        _rasterStore.GetRasterInfoAsync(1, 200, Arg.Any<CancellationToken>()).Returns(rasterB);
        _rasterStore.GetStatisticsAsync(1, 200, Arg.Any<int[]?>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new RasterStatistics
                {
                    Band = 1,
                    MinValue = 0,
                    MaxValue = 255,
                    MeanValue = 128,
                    StandardDeviation = 45,
                    ValidPixelCount = 1024,
                    NoDataPixelCount = 0,
                }
            });
        _rasterStore.GetHistogramsAsync(1, 200, Arg.Any<int[]?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new RasterHistogram
                {
                    Band = 1,
                    BinCount = 4,
                    Min = 0,
                    Max = 255,
                    Counts = [10, 20, 30, 40],
                }
            });

        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["rasterIds"] = "100,200",
        };

        var context = CreateImageServerContext();
        var result = await _handler.ComputeAsync(context, 1, values, CancellationToken.None);

        result.Should().BeOfType<JsonHttpResult<ComputeStatisticsHistogramsResponse>>();
        // Primary raster fallback must NOT be used when rasterIds is supplied.
        await _rasterStore.DidNotReceive().GetPrimaryRasterInfoAsync(1, Arg.Any<CancellationToken>());
        // Both catalog rasters should be analysed (no band filter when bandIds omitted).
        await _rasterStore.Received(1).GetStatisticsAsync(1, 100, null, Arg.Any<CancellationToken>());
        await _rasterStore.Received(1).GetStatisticsAsync(1, 200, null, Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ComputeAsync_RasterIdsJsonArray_LooksUpCatalogRastersAndComputesStatistics()
    {
        SetupSuccessfulCompute();
        var rasterA = CreateTestRasterInfo() with { Id = 100 };
        _rasterStore.GetRasterInfoAsync(1, 100, Arg.Any<CancellationToken>()).Returns(rasterA);

        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["rasterIds"] = "[100]",
        };

        var context = CreateImageServerContext();
        var result = await _handler.ComputeAsync(context, 1, values, CancellationToken.None);

        result.Should().BeOfType<JsonHttpResult<ComputeStatisticsHistogramsResponse>>();
        await _rasterStore.DidNotReceive().GetPrimaryRasterInfoAsync(1, Arg.Any<CancellationToken>());
        await _rasterStore.Received(1).GetStatisticsAsync(1, 100, null, Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ComputeAsync_BandIdsCsv_PassesBandsToStore()
    {
        SetupSuccessfulCompute();

        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["bandIds"] = "1,3",
        };

        var context = CreateImageServerContext();
        var result = await _handler.ComputeAsync(context, 1, values, CancellationToken.None);

        result.Should().BeOfType<JsonHttpResult<ComputeStatisticsHistogramsResponse>>();
        await _rasterStore.Received(1).GetStatisticsAsync(
            1,
            100,
            Arg.Is<int[]>(b => b != null && b.Length == 2 && b[0] == 1 && b[1] == 3),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ComputeAsync_BandIdsJsonArray_PassesBandsToStore()
    {
        SetupSuccessfulCompute();

        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["bandIds"] = "[2,4]",
        };

        var context = CreateImageServerContext();
        var result = await _handler.ComputeAsync(context, 1, values, CancellationToken.None);

        result.Should().BeOfType<JsonHttpResult<ComputeStatisticsHistogramsResponse>>();
        await _rasterStore.Received(1).GetStatisticsAsync(
            1,
            100,
            Arg.Is<int[]>(b => b != null && b.Length == 2 && b[0] == 2 && b[1] == 4),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ComputeAsync_RasterIdsNotInCatalog_ReturnsBadRequest()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());
        _rasterStore.GetRasterInfoAsync(1, 999, Arg.Any<CancellationToken>())
            .Returns((RasterInfo?)null);

        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["rasterIds"] = "999",
        };

        var context = CreateImageServerContext();
        var result = await _handler.ComputeAsync(context, 1, values, CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ComputeAsync_RasterIdsNonPositive_ReturnsBadRequest()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());

        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["rasterIds"] = "0,1",
        };

        var context = CreateImageServerContext();
        var result = await _handler.ComputeAsync(context, 1, values, CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ComputeAsync_BandIdsNonPositive_ReturnsBadRequest()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());

        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["bandIds"] = "0,1",
        };

        var context = CreateImageServerContext();
        var result = await _handler.ComputeAsync(context, 1, values, CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ComputeAsync_RasterStoreThrows_ReturnsServerError()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());
        _rasterStore.GetPrimaryRasterInfoAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestRasterInfo());
        _rasterStore.GetStatisticsAsync(1, 100, Arg.Any<int[]?>(), Arg.Any<CancellationToken>())
            .Returns<Task<RasterStatistics[]>>(_ => throw new InvalidOperationException("boom"));

        var context = CreateImageServerContext();
        var result = await _handler.ComputeAsync(context, 1, EmptyValues(), CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
    }

    private void SetupSuccessfulCompute()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());
        _rasterStore.GetPrimaryRasterInfoAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestRasterInfo());
        _rasterStore.GetStatisticsAsync(1, 100, Arg.Any<int[]?>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new RasterStatistics
                {
                    Band = 1,
                    MinValue = 0,
                    MaxValue = 255,
                    MeanValue = 128,
                    StandardDeviation = 45,
                    ValidPixelCount = 1024,
                    NoDataPixelCount = 0,
                }
            });
        _rasterStore.GetHistogramsAsync(1, 100, Arg.Any<int[]?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new RasterHistogram
                {
                    Band = 1,
                    BinCount = 4,
                    Min = 0,
                    Max = 255,
                    Counts = [10, 20, 30, 40],
                }
            });
    }

    private static Dictionary<string, StringValues> EmptyValues()
        => new(StringComparer.OrdinalIgnoreCase);

    private static DefaultHttpContext CreateImageServerContext()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);

        var context = new DefaultHttpContext();
        context.RequestServices = services.BuildServiceProvider();
        context.Request.Path = "/rest/services/1/ImageServer/computeStatisticsHistograms";
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
