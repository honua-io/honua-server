// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Infrastructure;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Server.Features.Protocols.GeoServices.ImageServer.Handlers;
using Honua.Server.Features.Protocols.GeoServices.ImageServer.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

/// <summary>
/// Tests for <see cref="ImageServerStatisticsHistogramsHandler"/>. Verifies the
/// internal whole-raster statistics/histogram helper retained behind the public
/// ImageServer contract until geometry-scoped support is implemented.
/// </summary>
[Protocol(TestProtocols.ImageServer)]
public class ImageServerStatisticsHistogramsHandlerTests
{
    private readonly TestMetadataV2GraphProvider _graphProvider = BuildGraphWithLayer(1);
    private readonly IRasterStore _rasterStore = Substitute.For<IRasterStore>();
    private readonly ImageServerStatisticsHistogramsHandler _handler;

    public ImageServerStatisticsHistogramsHandlerTests()
    {
        _handler = new ImageServerStatisticsHistogramsHandler(
            _graphProvider,
            _rasterStore,
            NullLogger<ImageServerStatisticsHistogramsHandler>.Instance);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ComputeAsync_LayerNotFound_ReturnsNotFound()
    {

        var context = CreateImageServerContext();
        var result = await _handler.ComputeAsync(context, 99, EmptyValues(), CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ComputeAsync_NoRasters_ReturnsNotFound()
    {
        _rasterStore.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs(Array.Empty<RasterInfo>());

        var context = CreateImageServerContext();
        var result = await _handler.ComputeAsync(context, 1, EmptyValues(), CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ComputeAsync_UnsupportedFormat_ReturnsBadRequest()
    {

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
        _rasterStore.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs([CreateTestRasterInfo()]);
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
        await _rasterStore.DidNotReceive().QueryRastersAsync(1, Arg.Any<RasterSelectionQuery>(), Arg.Any<CancellationToken>());
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
        await _rasterStore.DidNotReceive().QueryRastersAsync(1, Arg.Any<RasterSelectionQuery>(), Arg.Any<CancellationToken>());
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
    public async Task ComputeAsync_BandIdsNonAscending_AlignsStatisticsWithHistogramsByBand()
    {
        // Regression: GetStatisticsAsync streams cached rows in DB-ascending band order,
        // while GetHistogramsAsync preserves caller-supplied order. When the caller asks
        // for bandIds=3,1 the parallel statistics/histograms arrays would silently
        // misalign — the handler must reorder both to a single canonical order so that
        // statistics[i] and histograms[i] always describe the same band.
        _rasterStore.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs([CreateTestRasterInfo()]);

        // Stats: returned in DB ascending order (band 1, then band 3) — mimics the
        // PostgresRasterStore behaviour where the SQL ORDER BY band_number wins.
        _rasterStore.GetStatisticsAsync(1, 100, Arg.Any<int[]?>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new RasterStatistics
                {
                    Band = 1,
                    MinValue = 10,
                    MaxValue = 110,
                    MeanValue = 60,
                    StandardDeviation = 5,
                    ValidPixelCount = 100,
                },
                new RasterStatistics
                {
                    Band = 3,
                    MinValue = 30,
                    MaxValue = 330,
                    MeanValue = 180,
                    StandardDeviation = 15,
                    ValidPixelCount = 300,
                },
            });

        // Histograms: returned in caller-supplied order (band 3, then band 1).
        _rasterStore.GetHistogramsAsync(1, 100, Arg.Any<int[]?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new RasterHistogram
                {
                    Band = 3,
                    BinCount = 2,
                    Min = 30,
                    Max = 330,
                    Counts = [3, 33],
                },
                new RasterHistogram
                {
                    Band = 1,
                    BinCount = 2,
                    Min = 10,
                    Max = 110,
                    Counts = [1, 11],
                },
            });

        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["bandIds"] = "3,1",
        };

        var context = CreateImageServerContext();
        var result = await _handler.ComputeAsync(context, 1, values, CancellationToken.None);

        var jsonResult = result as JsonHttpResult<ComputeStatisticsHistogramsResponse>;
        jsonResult.Should().NotBeNull();
        var response = jsonResult!.Value!;

        response.Statistics.Should().HaveCount(2);
        response.Histograms.Should().HaveCount(2);

        // Caller-supplied order (3, 1) wins as the canonical order.
        // Position 0 must describe band 3 on both sides.
        response.Statistics[0].Min.Should().Be(30);
        response.Statistics[0].Max.Should().Be(330);
        response.Statistics[0].Count.Should().Be(300);
        response.Histograms[0].Min.Should().Be(30);
        response.Histograms[0].Max.Should().Be(330);
        response.Histograms[0].Counts.Should().Equal(3L, 33L);

        // Position 1 must describe band 1 on both sides.
        response.Statistics[1].Min.Should().Be(10);
        response.Statistics[1].Max.Should().Be(110);
        response.Statistics[1].Count.Should().Be(100);
        response.Histograms[1].Min.Should().Be(10);
        response.Histograms[1].Max.Should().Be(110);
        response.Histograms[1].Counts.Should().Equal(1L, 11L);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ComputeAsync_RasterIdsNotInCatalog_ReturnsBadRequest()
    {
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
        _rasterStore.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs([CreateTestRasterInfo()]);
        _rasterStore.GetStatisticsAsync(1, 100, Arg.Any<int[]?>(), Arg.Any<CancellationToken>())
            .Returns<Task<RasterStatistics[]>>(_ => throw new InvalidOperationException("boom"));

        var context = CreateImageServerContext();
        var result = await _handler.ComputeAsync(context, 1, EmptyValues(), CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
    }

    private void SetupSuccessfulCompute()
    {
        _rasterStore.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs([CreateTestRasterInfo()]);
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
        context.Request.Path = "/rest/services/1/ImageServer/_internal/computeStatisticsHistograms";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static TestMetadataV2GraphProvider BuildGraphWithLayer(int layerIndex)
        => new TestMetadataV2GraphBuilder()
            .AddResource($"resource-{layerIndex}", "test-layer", MetadataV2ResourceType.RasterDataset)
            .AddService($"service-{layerIndex}", $"image-svc-{layerIndex}", protocols: [ServiceProtocols.ImageServer])
            .AddPublication(
                $"publication-{layerIndex}",
                $"service-{layerIndex}",
                $"resource-{layerIndex}",
                layerIndex: layerIndex,
                serviceLocalId: "test-layer",
                publicationType: MetadataV2PublicationType.EsriImageLayer)
            .BuildProvider();

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
