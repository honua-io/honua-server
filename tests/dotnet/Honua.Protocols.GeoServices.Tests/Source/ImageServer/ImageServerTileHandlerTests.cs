// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer.Handlers;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

/// <summary>
/// Tests for ImageServerTileHandler functionality.
/// </summary>
[Protocol(TestProtocols.ImageServer)]
public class ImageServerTileHandlerTests
{
    private readonly TestMetadataV2GraphProvider _graphProvider = BuildGraphWithLayer(1);
    private readonly IRasterStore _rasterStore = Substitute.For<IRasterStore>();
    private readonly ImageServerTileHandler _handler;

    public ImageServerTileHandlerTests()
    {
        _handler = new ImageServerTileHandler(
            _graphProvider,
            _rasterStore,
            NullLogger<ImageServerTileHandler>.Instance);
    }

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetImageTileAsync_LayerNotFound_ReturnsNotFound()
    {

        var context = CreateImageServerContext();
        var result = await _handler.GetImageTileAsync(context, 99, 0, 0, 0, "png");
        await AssertGeoServicesErrorAsync(context, result, StatusCodes.Status404NotFound);
    }

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetImageTileAsync_NegativeLevel_ReturnsBadRequest()
    {

        var context = CreateImageServerContext();
        var result = await _handler.GetImageTileAsync(context, 1, -1, 0, 0, "png");
        await AssertGeoServicesErrorAsync(context, result, StatusCodes.Status400BadRequest);
    }

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetImageTileAsync_LevelExceedsMax_ReturnsBadRequest()
    {

        var context = CreateImageServerContext();
        var result = await _handler.GetImageTileAsync(context, 1, 29, 0, 0, "png");
        await AssertGeoServicesErrorAsync(context, result, StatusCodes.Status400BadRequest);
    }

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetImageTileAsync_RowExceedsBound_ReturnsBadRequest()
    {

        // At level 2, max row is 3 (2^2 - 1)
        var context = CreateImageServerContext();
        var result = await _handler.GetImageTileAsync(context, 1, 2, 4, 0, "png");
        await AssertGeoServicesErrorAsync(context, result, StatusCodes.Status400BadRequest);
    }

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetImageTileAsync_ColExceedsBound_ReturnsBadRequest()
    {

        // At level 1, max col is 1 (2^1 - 1)
        var context = CreateImageServerContext();
        var result = await _handler.GetImageTileAsync(context, 1, 1, 0, 2, "png");
        await AssertGeoServicesErrorAsync(context, result, StatusCodes.Status400BadRequest);
    }

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetImageTileAsync_UnsupportedFormat_ReturnsBadRequest()
    {

        var context = CreateImageServerContext();
        var result = await _handler.GetImageTileAsync(context, 1, 0, 0, 0, "bmp");
        await AssertGeoServicesErrorAsync(context, result, StatusCodes.Status400BadRequest);
        await _rasterStore.DidNotReceive()
            .QueryRastersAsync(Arg.Any<int>(), Arg.Any<RasterSelectionQuery>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetImageTileAsync_NoRasters_ReturnsNotFound()
    {
        _rasterStore.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs(Array.Empty<RasterInfo>());

        var context = CreateImageServerContext();
        var result = await _handler.GetImageTileAsync(context, 1, 0, 0, 0, "png");
        await AssertGeoServicesErrorAsync(context, result, StatusCodes.Status404NotFound);
    }

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetImageTileAsync_TileNotFound_ReturnsNotFound()
    {
        _rasterStore.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs([CreateTestRasterInfo()]);
        _rasterStore.GetImageTileAsync(1, 100, 0, 0, 0, RasterFormat.PNG, Arg.Any<CancellationToken>())
            .Returns((RasterResult?)null);

        var context = CreateImageServerContext();
        var result = await _handler.GetImageTileAsync(context, 1, 0, 0, 0, "png");
        await AssertGeoServicesErrorAsync(context, result, StatusCodes.Status404NotFound);
    }

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetImageTileAsync_ValidTile_ReturnsFileResult()
    {
        var tileData = new byte[] { 0x89, 0x50, 0x4E, 0x47 }; // PNG header
        _rasterStore.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs([CreateTestRasterInfo()]);
        _rasterStore.GetImageTileAsync(1, 100, 0, 0, 0, RasterFormat.PNG, Arg.Any<CancellationToken>())
            .Returns(new RasterResult
            {
                Data = tileData,
                ContentType = "image/png",
                Width = 256,
                Height = 256,
                Srid = 3857
            });

        var context = CreateImageServerContext();
        var result = await _handler.GetImageTileAsync(context, 1, 0, 0, 0, "png");

        result.Should().BeOfType<FileContentHttpResult>();
    }

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetImageTileAsync_CloudCacheHit_ReturnsStoredTileWithoutRendering()
    {
        var cachedTile = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0xCA, 0xFE };
        var storage = Substitute.For<ICloudFileStorage>();
        storage.Provider.Returns(CloudStorageProvider.AwsS3);
        storage.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new CloudFile
            {
                FileId = call.ArgAt<string>(0),
                FileName = "0-0-0.png",
                StoragePath = call.ArgAt<string>(0),
                ContentType = "image/png",
                SizeBytes = cachedTile.Length,
                UploadedAt = DateTimeOffset.UtcNow,
                Provider = CloudStorageProvider.AwsS3
            });
        storage.DownloadBytesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(cachedTile);
        _rasterStore.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs([CreateTestRasterInfo()]);

        var context = CreateImageServerContext(services => services.AddSingleton(storage));
        var result = await _handler.GetImageTileAsync(context, 1, 0, 0, 0, "png");

        var fileResult = result.Should().BeOfType<FileContentHttpResult>().Subject;
        fileResult.FileContents.ToArray().Should().Equal(cachedTile);
        fileResult.ContentType.Should().Be("image/png");
        await storage.Received(1).DownloadBytesAsync(
            Arg.Is<string>(key => key.Contains("imageserver/tiles", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await _rasterStore.DidNotReceive().GetImageTileAsync(
            Arg.Any<int>(),
            Arg.Any<long>(),
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<RasterFormat>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.GetTile)]
    public async Task GetImageTileAsync_CloudCacheMiss_WritesGeneratedTile()
    {
        var tileData = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var storage = Substitute.For<ICloudFileStorage>();
        storage.Provider.Returns(CloudStorageProvider.AwsS3);
        storage.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((CloudFile?)null);
        storage.UploadAsync(Arg.Any<FileUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.ArgAt<FileUploadRequest>(0);
                return UploadResult.CreateSuccess(new CloudFile
                {
                    FileId = request.ObjectKeyOverride!,
                    FileName = request.FileName,
                    StoragePath = request.ObjectKeyOverride!,
                    ContentType = request.ContentType,
                    SizeBytes = request.SizeBytes ?? 0,
                    UploadedAt = DateTimeOffset.UtcNow,
                    Provider = CloudStorageProvider.AwsS3,
                    Metadata = request.Metadata
                });
            });
        _rasterStore.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs([CreateTestRasterInfo()]);
        _rasterStore.GetImageTileAsync(1, 100, 0, 0, 0, RasterFormat.PNG, Arg.Any<CancellationToken>())
            .Returns(new RasterResult
            {
                Data = tileData,
                ContentType = "image/png",
                Width = 256,
                Height = 256,
                Srid = 3857
            });

        var options = Options.Create(new CloudStorageOptions
        {
            Provider = CloudStorageProvider.AwsS3,
            AwsS3 = new AwsS3Options
            {
                BucketName = "test-bucket",
                Region = "us-east-1",
                KeyPrefix = "geo-cache"
            }
        });
        var context = CreateImageServerContext(services =>
        {
            services.AddSingleton(storage);
            services.AddSingleton<IOptions<CloudStorageOptions>>(options);
        });

        var result = await _handler.GetImageTileAsync(context, 1, 0, 0, 0, "png");

        var fileResult = result.Should().BeOfType<FileContentHttpResult>().Subject;
        fileResult.FileContents.ToArray().Should().Equal(tileData);
        await storage.Received(1).UploadAsync(
            Arg.Is<FileUploadRequest>(request =>
                request.ObjectKeyOverride != null &&
                request.ObjectKeyOverride.StartsWith("geo-cache/imageserver/tiles/", StringComparison.Ordinal) &&
                request.ContentType == "image/png" &&
                request.FileName == "0-0-0.png" &&
                request.Metadata["protocol"] == "ImageServer" &&
                request.Metadata["operation"] == "tile"),
            Arg.Any<CancellationToken>());
    }

    [Trait("Category", "Unit")]
    [Operation(Operations.GetTile)]
    [Theory]
    [InlineData(0, 0, 0)]   // Level 0: 1x1 grid
    [InlineData(1, 0, 0)]   // Level 1: 2x2 grid
    [InlineData(1, 1, 1)]   // Level 1: max row/col
    [InlineData(2, 3, 3)]   // Level 2: 4x4 grid, max
    public async Task GetImageTileAsync_ValidCoordinates_DoesNotReturnBadRequest(int level, int row, int col)
    {
        _rasterStore.QueryRastersAsync(default, default, default)
            .ReturnsForAnyArgs([CreateTestRasterInfo()]);
        _rasterStore.GetImageTileAsync(1, 100, level, row, col, RasterFormat.PNG, Arg.Any<CancellationToken>())
            .Returns(new RasterResult
            {
                Data = [0x89],
                ContentType = "image/png",
                Width = 256,
                Height = 256,
                Srid = 3857
            });

        var context = CreateImageServerContext();
        var result = await _handler.GetImageTileAsync(context, 1, level, row, col, "png");
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().NotBe(StatusCodes.Status400BadRequest);
    }

    private static DefaultHttpContext CreateImageServerContext(Action<IServiceCollection>? configureServices = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        configureServices?.Invoke(services);
        var context = new DefaultHttpContext();
        context.RequestServices = services.BuildServiceProvider();
        context.Request.Path = "/rest/services/1/ImageServer/tile/0/0/0";
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
