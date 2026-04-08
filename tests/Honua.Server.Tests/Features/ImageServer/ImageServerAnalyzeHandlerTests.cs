// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Server.Features.ImageServer.Handlers;
using Honua.Server.Features.ImageServer.Models;
using Honua.Server.Features.ImageServer.Services;
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
/// Tests for <see cref="ImageServerAnalyzeHandler"/>. Verifies that the
/// computeClass / analyze endpoint validates a raster function chain document
/// and returns the planned execution metadata.
/// </summary>
[Protocol(Protocols.ImageServer)]
public class ImageServerAnalyzeHandlerTests
{
    private readonly ILayerCatalog _layerCatalog = Substitute.For<ILayerCatalog>();
    private readonly ImageServerAnalyzeHandler _handler;

    public ImageServerAnalyzeHandlerTests()
    {
        _handler = new ImageServerAnalyzeHandler(
            _layerCatalog,
            new ImageServerRasterFunctionPlanner(),
            NullLogger<ImageServerAnalyzeHandler>.Instance);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task AnalyzeAsync_LayerNotFound_ReturnsNotFound()
    {
        _layerCatalog.GetLayerAsync(99, Arg.Any<CancellationToken>())
            .Returns((LayerDefinition?)null);

        var context = CreateImageServerContext();
        var result = await _handler.AnalyzeAsync(
            context,
            99,
            Values(("renderingRule", "{\"rasterFunction\":\"Identity\"}")),
            CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task AnalyzeAsync_MissingRenderingRule_ReturnsBadRequest()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());

        var context = CreateImageServerContext();
        var result = await _handler.AnalyzeAsync(context, 1, EmptyValues(), CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task AnalyzeAsync_RasterFunctionParameter_IsAccepted()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());

        var context = CreateImageServerContext();
        var result = await _handler.AnalyzeAsync(
            context,
            1,
            Values(("rasterFunction", "{\"rasterFunction\":\"Identity\"}")),
            CancellationToken.None);

        var jsonResult = result as JsonHttpResult<AnalyzeResponse>;
        jsonResult.Should().NotBeNull();
        jsonResult!.Value!.RasterFunction.Should().Be("Identity");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task AnalyzeAsync_InvalidJson_ReturnsBadRequest()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());

        var context = CreateImageServerContext();
        var result = await _handler.AnalyzeAsync(
            context,
            1,
            Values(("renderingRule", "{not-json")),
            CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task AnalyzeAsync_UnsupportedFunction_ReturnsBadRequest()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());

        var context = CreateImageServerContext();
        var result = await _handler.AnalyzeAsync(
            context,
            1,
            Values(("renderingRule", "{\"rasterFunction\":\"Hillshade\"}")),
            CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task AnalyzeAsync_StretchWithoutStretchType_ReturnsBadRequest()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());

        var context = CreateImageServerContext();
        var result = await _handler.AnalyzeAsync(
            context,
            1,
            Values(("renderingRule", "{\"rasterFunction\":\"Stretch\",\"rasterFunctionArguments\":{}}")),
            CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task AnalyzeAsync_StretchWithStretchType_ReturnsSuccess()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());

        var context = CreateImageServerContext();
        var result = await _handler.AnalyzeAsync(
            context,
            1,
            Values(("renderingRule", "{\"rasterFunction\":\"Stretch\",\"rasterFunctionArguments\":{\"StretchType\":3}}")),
            CancellationToken.None);

        var jsonResult = result as JsonHttpResult<AnalyzeResponse>;
        jsonResult.Should().NotBeNull();
        jsonResult!.Value!.ChainDepth.Should().Be(1);
        jsonResult.Value.ExecutedFunctions.Should().Equal("Stretch");
        jsonResult.Value.OutputPixelType.Should().Be("U8");
        jsonResult.Value.Status.Should().Be("success");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task AnalyzeAsync_ClipWithoutGeometryOrExtent_ReturnsBadRequest()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());

        var context = CreateImageServerContext();
        var result = await _handler.AnalyzeAsync(
            context,
            1,
            Values(("renderingRule", "{\"rasterFunction\":\"Clip\",\"rasterFunctionArguments\":{}}")),
            CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task AnalyzeAsync_NestedChainStretchOverClip_ReportsCorrectDepth()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());

        const string chain = """
        {
          "rasterFunction": "Stretch",
          "rasterFunctionArguments": {
            "StretchType": 3,
            "Raster": {
              "rasterFunction": "Clip",
              "rasterFunctionArguments": {
                "ClippingGeometry": {"rings": []}
              }
            }
          }
        }
        """;

        var context = CreateImageServerContext();
        var result = await _handler.AnalyzeAsync(
            context,
            1,
            Values(("renderingRule", chain)),
            CancellationToken.None);

        var jsonResult = result as JsonHttpResult<AnalyzeResponse>;
        jsonResult.Should().NotBeNull();
        jsonResult!.Value!.ChainDepth.Should().Be(2);
        jsonResult.Value.ExecutedFunctions.Should().Equal("Stretch", "Clip");
        jsonResult.Value.OutputPixelType.Should().Be("U8");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task AnalyzeAsync_IdentityFunction_ReturnsF32PixelType()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());

        var context = CreateImageServerContext();
        var result = await _handler.AnalyzeAsync(
            context,
            1,
            Values(("renderingRule", "{\"rasterFunction\":\"Identity\"}")),
            CancellationToken.None);

        var jsonResult = result as JsonHttpResult<AnalyzeResponse>;
        jsonResult.Should().NotBeNull();
        jsonResult!.Value!.OutputPixelType.Should().Be("F32");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task AnalyzeAsync_ExceedingMaxDepth_ReturnsBadRequest()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());

        // Build an Identity-over-Identity chain deeper than the limit (8).
        var chain = "{\"rasterFunction\":\"Identity\"}";
        for (var i = 0; i < ImageServerRasterFunctionPlanner.MaxChainDepth + 1; i++)
        {
            chain = "{\"rasterFunction\":\"Identity\",\"rasterFunctionArguments\":{\"Raster\":" + chain + "}}";
        }

        var context = CreateImageServerContext();
        var result = await _handler.AnalyzeAsync(
            context,
            1,
            Values(("renderingRule", chain)),
            CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task AnalyzeAsync_OutputPixelTypeOverride_HonoursDocument()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());

        var context = CreateImageServerContext();
        var result = await _handler.AnalyzeAsync(
            context,
            1,
            Values(("renderingRule", "{\"rasterFunction\":\"Identity\",\"outputPixelType\":\"S16\"}")),
            CancellationToken.None);

        var jsonResult = result as JsonHttpResult<AnalyzeResponse>;
        jsonResult.Should().NotBeNull();
        jsonResult!.Value!.OutputPixelType.Should().Be("S16");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task AnalyzeAsync_UnsupportedFormat_ReturnsBadRequest()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());

        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["renderingRule"] = "{\"rasterFunction\":\"Identity\"}",
            ["f"] = "html",
        };

        var context = CreateImageServerContext();
        var result = await _handler.AnalyzeAsync(context, 1, values, CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    private static Dictionary<string, StringValues> Values(params (string Key, string Value)[] entries)
    {
        var dict = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            dict[entry.Key] = entry.Value;
        }
        return dict;
    }

    private static Dictionary<string, StringValues> EmptyValues()
        => new(StringComparer.OrdinalIgnoreCase);

    private static DefaultHttpContext CreateImageServerContext()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);

        var context = new DefaultHttpContext();
        context.RequestServices = services.BuildServiceProvider();
        context.Request.Path = "/rest/services/1/ImageServer/computeClass";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static LayerDefinition CreateTestLayer()
        => LayerDefinition.CreateBasic(1, "test-layer", GeometryType.Point);
}
