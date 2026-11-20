// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Tiles;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Services;
using Honua.Protocols.GeoServices.ImageServer.Handlers;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SkiaSharp;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

// exportImage on a layer whose imagery is only a registered cloud COG composes the tiles the tile
// route serves, so a dynamic ImageryLayer draws it instead of getting "No rasters found".
[Protocol(TestProtocols.ImageServer)]
public sealed class ImageServerCogExportTests
{
    // The bounded-roster COG: 512 x 512 at 38.2185 m, 256-pixel tiles, so the full grid is one
    // zoom-12 tile per COG tile (2 x 2 at x 654-655, y 1582-1583) and its overview is z11 tile 327/791.
    private static readonly RasterExtent CogExtent = new()
    {
        XMin = -13638811.83098057,
        YMin = 4539747.983913188,
        XMax = -13619243.951739565,
        YMax = 4559315.863154193,
    };

    private static readonly string FullExtentBbox = string.Join(
        ',',
        new[] { CogExtent.XMin, CogExtent.YMin, CogExtent.XMax, CogExtent.YMax }.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));

    private readonly IRasterStore _rasterStore = Substitute.For<IRasterStore>();
    private readonly ICogStore _cogStore = Substitute.For<ICogStore>();
    private readonly ICogTileResolver _tileResolver = Substitute.For<ICogTileResolver>();
    private readonly ITemporaryFileService _temporaryFiles = Substitute.For<ITemporaryFileService>();
    private readonly ImageServerExportHandler _handler;

    public ImageServerCogExportTests()
    {
        _rasterStore.QueryRastersAsync(default, default, default).ReturnsForAnyArgs(Array.Empty<RasterInfo>());
        _cogStore.ListByLayerAsync(1, Arg.Any<CancellationToken>()).Returns(new[] { Registration() });
        _handler = new ImageServerExportHandler(
            ImageServerTestFixtures.BuildGraphWithLayer(1),
            new ImageServerExportBackend(_rasterStore, Substitute.For<IZarrRasterSliceReader>()),
            _temporaryFiles,
            NullLogger<ImageServerExportHandler>.Instance);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_CogOnlyLayer_ComposesTheOverviewTileAtTheRequestedResolution()
    {
        _tileResolver.GetTileAsync(Arg.Any<CogRegistration>(), 11, 791, 327, RasterFormat.PNG, Arg.Any<CancellationToken>())
            .Returns(SolidPngResult(SKColors.Red));

        var result = await _handler.ExportImageAsync(CreateContext(), 1, Request(FullExtentBbox, size: 256));

        using var image = SKBitmap.Decode(result.Should().BeOfType<FileContentHttpResult>().Which.FileContents.ToArray());
        image.Width.Should().Be(256);
        image.GetPixel(128, 128).Should().Be(SKColors.Red);
        await _tileResolver.ReceivedWithAnyArgs(1).GetTileAsync(default!, default, default, default, default, default);
        await _rasterStore.DidNotReceiveWithAnyArgs().ExportEmptyExtentAsync(default, default, default);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_CogOnlyLayerZoomedIn_PlacesNativeTilesInTheirQuadrants()
    {
        _tileResolver.GetTileAsync(Arg.Any<CogRegistration>(), 12, Arg.Any<int>(), Arg.Any<int>(), RasterFormat.PNG, Arg.Any<CancellationToken>())
            .Returns(SolidPngResult(SKColors.Blue));
        _tileResolver.GetTileAsync(Arg.Any<CogRegistration>(), 12, 1582, 654, RasterFormat.PNG, Arg.Any<CancellationToken>())
            .Returns(SolidPngResult(SKColors.Red));

        var result = await _handler.ExportImageAsync(CreateContext(), 1, Request(FullExtentBbox, size: 512));

        using var image = SKBitmap.Decode(result.Should().BeOfType<FileContentHttpResult>().Which.FileContents.ToArray());
        image.GetPixel(64, 64).Should().Be(SKColors.Red, "tile 654/1582 is the north-west quarter");
        image.GetPixel(448, 448).Should().Be(SKColors.Blue);
        await _tileResolver.ReceivedWithAnyArgs(4).GetTileAsync(default!, default, default, default, default, default);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_CogOnlyLayerAsJson_DescribesTheWebMercatorExtent()
    {
        _temporaryFiles.StoreTemporaryFileAsync(default!, default!, default, default, default)
            .ReturnsForAnyArgs("https://example.test/export.png");

        var request = new ExportImageRequest { Bbox = FullExtentBbox, BboxSr = "102100", Size = "256,256", Format = "png", F = "json" };

        var result = await _handler.ExportImageAsync(CreateContext(), 1, request);

        var response = result.Should().BeOfType<JsonHttpResult<ExportImageResponse>>().Which.Value!;
        response.Href.Should().Be("https://example.test/export.png");
        response.Extent!.XMin.Should().Be(CogExtent.XMin);
        response.Extent.SpatialReference.Wkid.Should().Be(3857);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_CogOnlyLayerWithoutEntitlement_AnswersPaymentRequiredLikeTiles()
    {
        var licensing = Substitute.For<ILicenseEntitlementService>();
        licensing.CheckEntitlement(ImageServerCogRasters.Entitlement).Returns(new LicenseEntitlementDecision(
            ImageServerCogRasters.Entitlement, false, default, default, RequiredEdition: null, UpgradeMessage: "not active"));
        var context = CreateContext(services => services.AddSingleton(licensing));

        var result = await _handler.ExportImageAsync(context, 1, Request(FullExtentBbox, size: 256));

        await AssertGeoServicesErrorAsync(context, result, StatusCodes.Status402PaymentRequired);
        await _tileResolver.DidNotReceiveWithAnyArgs().GetTileAsync(default!, default, default, default, default, default);
    }

    [UnitTheory]
    [InlineData("lerc", "102100")]
    [InlineData("tiff", "102100")]
    [InlineData("png", "4326")]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_CogOnlyLayerOutsideWhatTilesCompose_AnswersNotImplemented(string format, string bboxSr)
    {
        var context = CreateContext();
        var request = new ExportImageRequest { Bbox = FullExtentBbox, BboxSr = bboxSr, Size = "256,256", Format = format, F = "image" };

        var result = await _handler.ExportImageAsync(context, 1, request);

        await AssertGeoServicesErrorAsync(context, result, StatusCodes.Status501NotImplemented);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_CogTooLargeForItsCoarsestOverview_AnswersBadRequest()
    {
        // 16 x 16 zoom-12 tiles with no overview: a full-extent export needs 256 tile reads.
        var northWest = TileMath.GetTileBounds(640, 1570, 12);
        var southEast = TileMath.GetTileBounds(655, 1585, 12);
        var extent = new RasterExtent { XMin = northWest.XMin, YMax = northWest.YMax, XMax = southEast.XMax, YMin = southEast.YMin };
        _cogStore.ListByLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { Registration(new(4096, 4096, 3, "uint8", 3857, "DEFLATE", 256, 256, [Overview(4096)], extent)) });
        var context = CreateContext();
        var bbox = FormattableString.Invariant($"{extent.XMin},{extent.YMin},{extent.XMax},{extent.YMax}");

        var result = await _handler.ExportImageAsync(context, 1, Request(bbox, size: 4096));

        await AssertGeoServicesErrorAsync(context, result, StatusCodes.Status400BadRequest);
        await _tileResolver.DidNotReceiveWithAnyArgs().GetTileAsync(default!, default, default, default, default, default);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_FramedBboxOutsideTheCog_AnswersAnEmptyImageWithoutReadingTiles()
    {
        // A tiling client's grid cells far from the COG still get an image, not a tile-cap error.
        var bbox = FormattableString.Invariant($"{CogExtent.XMax + 1_000_000},{CogExtent.YMin},{CogExtent.XMax + 2_000_000},{CogExtent.YMax + 1_000_000}");

        var result = await _handler.ExportImageAsync(CreateContext(), 1, Request(bbox, size: 256));

        using var image = SKBitmap.Decode(result.Should().BeOfType<FileContentHttpResult>().Which.FileContents.ToArray());
        image.GetPixel(128, 128).Alpha.Should().Be(0);
        await _tileResolver.DidNotReceiveWithAnyArgs().GetTileAsync(default!, default, default, default, default, default);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_LayerHoldingRasterData_KeepsTheRasterDataAnswerForAnEmptySelection()
    {
        // raster_data wins: a bbox missing its footprint gets the empty-extent image, not COG pixels.
        _rasterStore.GetPrimaryRasterInfoAsync(1, Arg.Any<CancellationToken>()).Returns(ImageServerTestFixtures.CreateSmallRasterInfo(bandCount: 1));
        _rasterStore.ExportEmptyExtentAsync(1, Arg.Any<RasterQuery>(), Arg.Any<CancellationToken>())
            .Returns(new RasterResult { Data = [1, 2, 3], ContentType = "image/png", Width = 256, Height = 256 });

        var result = await _handler.ExportImageAsync(CreateContext(), 1, Request(FullExtentBbox, size: 256));

        result.Should().BeOfType<FileContentHttpResult>().Which.FileContents.ToArray().Should().Equal(1, 2, 3);
        await _tileResolver.DidNotReceiveWithAnyArgs().GetTileAsync(default!, default, default, default, default, default);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExportImageAsync_NoCogTileResolver_KeepsTheRasterDataNotFound()
    {
        var context = CreateContextWithoutResolver();

        var result = await _handler.ExportImageAsync(context, 1, Request(bbox: null, size: null));

        await AssertGeoServicesErrorAsync(context, result, StatusCodes.Status404NotFound);
        await _cogStore.DidNotReceiveWithAnyArgs().ListByLayerAsync(default, default);
    }

    [UnitTest]
    public void ServableZooms_GridAlignedOverviews_AreTheirTileZooms()
    {
        ImageServerCogExport.CogCanvas.ServableZooms([Registration()]).Should().Equal(11, 12);
        ImageServerCogExport.CogCanvas.ChooseZoom([11, 12], needed: 14).Should().Be(12, "a request finer than every overview reads the finest");
        ImageServerCogExport.CogCanvas.ChooseZoom([11, 12], needed: 9).Should().Be(11);
    }

    private static byte[] SolidPng(SKColor color)
    {
        using var bitmap = new SKBitmap(256, 256);
        bitmap.Erase(color);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static RasterResult? SolidPngResult(SKColor color)
        => new RasterResult { Data = SolidPng(color), ContentType = "image/png", Width = 256, Height = 256 };

    private static CogOverviewLevel Overview(int width) => new(0, width, width, 0, [], []);

    private static CogMetadata CogMetadata()
        => new(512, 512, 3, "uint8", 3857, "DEFLATE", 256, 256, [Overview(512), Overview(256)], CogExtent);

    private static CogRegistration Registration(CogMetadata? metadata = null) => new()
    {
        Id = 7,
        LayerId = 1,
        Name = "roster cog",
        Provider = CloudStorageProvider.AwsS3,
        Bucket = "roster-fixtures",
        ObjectKey = "cog/roster-3857.tif",
        Metadata = metadata ?? CogMetadata(),
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    // A png drawn inline, the way a dynamic ImageryLayer asks on a Web Mercator map.
    private static ExportImageRequest Request(string? bbox, int? size) => new()
    {
        Bbox = bbox,
        BboxSr = bbox is null ? null : "102100",
        Size = size is { } s ? FormattableString.Invariant($"{s},{s}") : null,
        Format = "png",
        F = "image",
    };

    private DefaultHttpContext CreateContext(Action<IServiceCollection>? configure = null)
        => BuildContext(services =>
        {
            services.AddSingleton(_tileResolver);
            configure?.Invoke(services);
        });

    private DefaultHttpContext CreateContextWithoutResolver() => BuildContext(_ => { });

    private DefaultHttpContext BuildContext(Action<IServiceCollection> configure)
    {
        var context = ImageServerTestFixtures.CreateContext("/rest/services/1/ImageServer/exportImage", services =>
        {
            services.AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>();
            services.AddSingleton(_rasterStore);
            services.AddSingleton(_cogStore);
            configure(services);
        });
        context.User = new ClaimsPrincipal(new ClaimsIdentity([], "test"));
        return context;
    }
}
