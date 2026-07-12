// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.Services;
using Honua.Protocols.GeoServices.ImageServer;
using Honua.Protocols.GeoServices.ImageServer.Handlers;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

/// <summary>
/// Unit tests for <see cref="ImageServerComputeClassStatisticsHandler"/>. Exercises the class
/// signature pipeline over synthetic per-pixel band vectors returned by a substituted raster store:
/// per-class count, per-band mean, and the covariance signature are asserted against hand-computed
/// values, and unsupported request combinations are explicitly rejected.
/// </summary>
[Protocol(TestProtocols.ImageServer)]
public sealed class ImageServerComputeClassStatisticsHandlerTests
{
    private const string TwoClassDescriptions =
        """{"classes":[{"id":1,"name":"veg","geometry":{"rings":[[[-1,-1],[-1,1],[1,1],[1,-1],[-1,-1]]]}},{"id":2,"name":"water","geometry":{"rings":[[[2,2],[2,3],[3,3],[3,2],[2,2]]]}}]}""";

    private readonly TestMetadataV2GraphProvider _graphProvider = BuildGraphWithLayer(1);
    private readonly IRasterStore _rasterStore = Substitute.For<IRasterStore>();
    private readonly ImageServerComputeClassStatisticsHandler _handler;

    public ImageServerComputeClassStatisticsHandlerTests()
    {
        _handler = new ImageServerComputeClassStatisticsHandler(
            _graphProvider,
            _rasterStore,
            new RasterClassStatisticsAnalyzer(_rasterStore),
            Options.Create(new ImageServerClassStatisticsOptions()),
            NullLogger<ImageServerComputeClassStatisticsHandler>.Instance);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ComputeAsync_TwoClasses_ReturnsHandComputedSignatures()
    {
        _rasterStore.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs([CreateTestRasterInfo()]);

        // Class 1: band1=[1,2,3,4], band2=[2,4,6,8]. Class 2: band1=[5,7], band2=[5,7].
        var callCount = 0;
        _rasterStore.ReadClippedBandVectorsAsync(
                Arg.Any<int>(), Arg.Any<long[]>(), Arg.Any<RasterMergeStrategy>(),
                Arg.Any<byte[]>(), Arg.Any<int?>(), Arg.Any<int[]?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount == 1
                    ? new RasterBandVectorSet
                    {
                        Bands = [1, 2],
                        Pixels = [[1.0, 2.0], [2.0, 4.0], [3.0, 6.0], [4.0, 8.0]],
                    }
                    : new RasterBandVectorSet
                    {
                        Bands = [1, 2],
                        Pixels = [[5.0, 5.0], [7.0, 7.0]],
                    };
            });

        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["classDescriptions"] = TwoClassDescriptions,
            ["bandIds"] = "1,2",
        };

        var result = await _handler.ComputeAsync(CreateImageServerContext(), 1, values, CancellationToken.None);

        var json = result as JsonHttpResult<ComputeClassStatisticsResponse>;
        json.Should().NotBeNull();
        var classes = json!.Value!.ClassStatistics;
        classes.Should().HaveCount(2);

        // Class 1 — mean (2.5, 5), sample covariance [[5/3, 10/3],[10/3, 20/3]].
        var c1 = classes[0];
        c1.ClassId.Should().Be(1);
        c1.Name.Should().Be("veg");
        c1.Count.Should().Be(4);
        c1.Bands.Should().Equal(1, 2);
        c1.Mean[0].Should().BeApproximately(2.5, 1e-9);
        c1.Mean[1].Should().BeApproximately(5.0, 1e-9);
        c1.CovarianceMatrix[0][0].Should().BeApproximately(5.0 / 3.0, 1e-9);
        c1.CovarianceMatrix[0][1].Should().BeApproximately(10.0 / 3.0, 1e-9);
        c1.CovarianceMatrix[1][0].Should().BeApproximately(10.0 / 3.0, 1e-9);
        c1.CovarianceMatrix[1][1].Should().BeApproximately(20.0 / 3.0, 1e-9);
        c1.Min.Should().Equal(1.0, 2.0);
        c1.Max.Should().Equal(4.0, 8.0);

        // Class 2 — mean (6, 6), sample covariance (n-1=1) [[2,2],[2,2]].
        var c2 = classes[1];
        c2.ClassId.Should().Be(2);
        c2.Count.Should().Be(2);
        c2.Mean[0].Should().BeApproximately(6.0, 1e-9);
        c2.Mean[1].Should().BeApproximately(6.0, 1e-9);
        c2.CovarianceMatrix[0][0].Should().BeApproximately(2.0, 1e-9);
        c2.CovarianceMatrix[0][1].Should().BeApproximately(2.0, 1e-9);
        c2.CovarianceMatrix[1][1].Should().BeApproximately(2.0, 1e-9);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ComputeAsync_LayerNotFound_ReturnsNotFound()
    {
        var context = CreateImageServerContext();
        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["classDescriptions"] = TwoClassDescriptions,
        };

        var result = await _handler.ComputeAsync(context, 99, values, CancellationToken.None);

        (await ExecuteAndReadErrorCodeAsync(result, context)).Should().Be(404);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ComputeAsync_AoiExceedsPixelBudget_ReturnsBadRequest()
    {
        _rasterStore.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs([CreateTestRasterInfo()]);
        _rasterStore.ReadClippedBandVectorsAsync(
                Arg.Any<int>(), Arg.Any<long[]>(), Arg.Any<RasterMergeStrategy>(),
                Arg.Any<byte[]>(), Arg.Any<int?>(), Arg.Any<int[]?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new RasterBandVectorSet
            {
                Bands = [1],
                Pixels = Array.Empty<double[]>(),
                ExceededPixelBudget = true,
                BoundingPixelCount = 99_999_999,
            });

        var context = CreateImageServerContext();
        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["classDescriptions"] = TwoClassDescriptions,
        };

        var result = await _handler.ComputeAsync(context, 1, values, CancellationToken.None);

        // The over-budget AOI is rejected as a 400 (GeoServices code in the response body).
        (await ExecuteAndReadErrorCodeAsync(result, context)).Should().Be(400);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ComputeAsync_RenderingRule_ReturnsNotImplemented()
    {
        var context = CreateImageServerContext();
        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["classDescriptions"] = TwoClassDescriptions,
            ["renderingRule"] = """{"rasterFunction":"Stretch","rasterFunctionArguments":{"StretchType":5}}""",
        };

        var result = await _handler.ComputeAsync(context, 1, values, CancellationToken.None);

        // renderingRule is explicitly rejected (NotImplemented). The GeoServices error body maps
        // HTTP 501 to code 500 (Esri has no 501 code), matching the endpoint test's 501/500 tolerance.
        (await ExecuteAndReadErrorCodeAsync(result, context)).Should().BeOneOf(501, 500);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task ComputeAsync_ClassWithoutGeometry_ReturnsBadRequest()
    {
        var context = CreateImageServerContext();
        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["classDescriptions"] = """{"classes":[{"id":1,"name":"veg"}]}""",
        };

        var result = await _handler.ComputeAsync(context, 1, values, CancellationToken.None);

        (await ExecuteAndReadErrorCodeAsync(result, context)).Should().Be(400);
    }

    // GeoServices errors are HTTP 200 with the code in the JSON body {"error":{"code":N,...}};
    // execute the result and read that body code.
    private static async Task<int> ExecuteAndReadErrorCodeAsync(IResult result, DefaultHttpContext context)
    {
        await result.ExecuteAsync(context);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return document.RootElement.GetProperty("error").GetProperty("code").GetInt32();
    }

    private static DefaultHttpContext CreateImageServerContext()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);

        var context = new DefaultHttpContext();
        context.RequestServices = services.BuildServiceProvider();
        context.Request.Path = "/rest/services/1/ImageServer/_internal/computeClassStatistics";
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
        BandCount = 2,
        PixelType = "8BUI",
        Srid = 4326,
        GeoTransform = [0, 1, 0, 0, 0, -1],
        Extent = new RasterExtent { XMin = -1, YMin = -1, XMax = 1, YMax = 1, Srid = 4326 },
        CreatedAt = DateTime.UtcNow,
    };
}
