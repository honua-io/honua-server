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

namespace Honua.Server.Tests.Features.ImageServer;

[Protocol(Protocols.ImageServer)]
public class ImageServerIdentifyHandlerTests
{
    private readonly ILayerCatalog _layerCatalog = Substitute.For<ILayerCatalog>();
    private readonly IRasterStore _rasterStore = Substitute.For<IRasterStore>();
    private readonly ImageServerIdentifyHandler _handler;

    public ImageServerIdentifyHandlerTests()
    {
        _handler = new ImageServerIdentifyHandler(
            _layerCatalog,
            _rasterStore,
            NullLogger<ImageServerIdentifyHandler>.Instance);
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_LayerNotFound_ReturnsNotFound()
    {
        _layerCatalog.GetLayerAsync(99, Arg.Any<CancellationToken>())
            .Returns((LayerDefinition?)null);

        var request = CreateRequest("10,20");
        var result = await _handler.IdentifyAsync(99, request);

        result.Should().BeOfType<NotFound>();
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_NoRasters_ReturnsNotFound()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());
        _rasterStore.ListRastersAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<RasterInfo>());

        var request = CreateRequest("10,20");
        var result = await _handler.IdentifyAsync(1, request);

        result.Should().BeOfType<NotFound>();
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_InvalidGeometryString_ReturnsBadRequest()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());
        _rasterStore.ListRastersAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { CreateTestRasterInfo() });

        var request = CreateRequest("invalid-geometry");
        var result = await _handler.IdentifyAsync(1, request);

        result.Should().BeOfType<BadRequest<string>>();
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_CommaCoordinates_ReturnsOk()
    {
        SetupSuccessfulIdentify();

        var request = CreateRequest("10.5,20.3");
        var result = await _handler.IdentifyAsync(1, request);

        result.Should().BeOfType<Ok<IdentifyResponse>>();
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_JsonGeometry_ReturnsOk()
    {
        SetupSuccessfulIdentify();

        var request = CreateRequest("{\"x\":10.5,\"y\":20.3}");
        var result = await _handler.IdentifyAsync(1, request);

        result.Should().BeOfType<Ok<IdentifyResponse>>();
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_JsonGeometryTooLarge_ReturnsBadRequest()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());
        _rasterStore.ListRastersAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { CreateTestRasterInfo() });

        // JSON geometry exceeding 1000 char limit
        var padding = new string(' ', 1001);
        var request = CreateRequest($"{{\"x\":10,\"y\":20,\"padding\":\"{padding}\"}}");
        var result = await _handler.IdentifyAsync(1, request);

        result.Should().BeOfType<BadRequest<string>>();
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_InvalidJson_ReturnsBadRequest()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());
        _rasterStore.ListRastersAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { CreateTestRasterInfo() });

        var request = CreateRequest("{invalid-json}");
        var result = await _handler.IdentifyAsync(1, request);

        result.Should().BeOfType<BadRequest<string>>();
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_ValidRequest_ResponseContainsBandValues()
    {
        SetupSuccessfulIdentify();

        var request = CreateRequest("10,20");
        var result = await _handler.IdentifyAsync(1, request);

        var okResult = result as Ok<IdentifyResponse>;
        okResult.Should().NotBeNull();
        okResult!.Value!.Properties.Should().ContainKey("BandCount");
        okResult.Value.Properties.Should().ContainKey("HasData");
    }

    private void SetupSuccessfulIdentify()
    {
        _layerCatalog.GetLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateTestLayer());
        _rasterStore.ListRastersAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { CreateTestRasterInfo() });
        _rasterStore.IdentifyAsync(1, 100, Arg.Any<double>(), Arg.Any<double>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new PixelValueResult
            {
                X = 10.5,
                Y = 20.3,
                Srid = 4326,
                HasData = true,
                BandValues = new Dictionary<int, object?> { [1] = 128.0, [2] = 64.0, [3] = 32.0 }
            });
    }

    private static IdentifyRequest CreateRequest(string geometry, string? sr = null) => new()
    {
        Geometry = geometry,
        GeometryType = "esriGeometryPoint",
        Sr = sr,
        F = "json"
    };

    private static LayerDefinition CreateTestLayer()
        => LayerDefinition.CreateBasic(1, "test-layer", GeometryType.Point);

    private static RasterInfo CreateTestRasterInfo() => new()
    {
        Id = 100,
        LayerId = 1,
        Name = "test-raster",
        Width = 1024,
        Height = 1024,
        BandCount = 3,
        PixelType = "uint8",
        Srid = 4326,
        GeoTransform = [0, 1, 0, 0, 0, -1],
        Extent = new RasterExtent { XMin = -180, YMin = -90, XMax = 180, YMax = 90, Srid = 4326 },
        CreatedAt = DateTime.UtcNow
    };
}
