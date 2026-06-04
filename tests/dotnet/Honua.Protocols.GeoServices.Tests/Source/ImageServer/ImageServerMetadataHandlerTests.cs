// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Infrastructure;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
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
using NSubstitute.ExceptionExtensions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

/// <summary>
/// Tests for ImageServerMetadataHandler functionality.
/// </summary>
[Protocol(TestProtocols.ImageServer)]
public class ImageServerMetadataHandlerTests
{
    private readonly TestMetadataV2GraphProvider _graphProvider = BuildGraphWithLayer(1);
    private readonly IRasterStore _rasterStore = Substitute.For<IRasterStore>();
    private readonly IImageServerMultidimensionalInfoBuilder _multidimensionalInfoBuilder =
        Substitute.For<IImageServerMultidimensionalInfoBuilder>();
    private readonly ImageServerMetadataHandler _handler;

    public ImageServerMetadataHandlerTests()
    {
        _multidimensionalInfoBuilder
            .BuildAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((ImageServerMultidimensionalInfo?)null);

        _handler = new ImageServerMetadataHandler(
            _graphProvider,
            _rasterStore,
            _multidimensionalInfoBuilder,
            NullLogger<ImageServerMetadataHandler>.Instance);
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfoAsync_LayerNotFound_ReturnsNotFound()
    {

        var context = CreateImageServerContext();
        var result = await _handler.GetServiceInfoAsync(context, 99);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfoAsync_NoRasters_ReturnsNotFound()
    {
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
    public async Task GetServiceInfoAsync_ResponseEmitsNonNullFieldsWithOid()
    {
        // The ArcGIS Maps SDK for JavaScript calls fields.find(...) during
        // ImageryLayer.load(); a null fields array makes it throw. Assert the
        // metadata emits the standard raster-catalog fields including the OID.
        SetupSuccessfulMetadata();

        var context = CreateImageServerContext();
        var result = await _handler.GetServiceInfoAsync(context, 1);

        var jsonResult = result as JsonHttpResult<ImageServerServiceInfo>;
        jsonResult.Should().NotBeNull();
        jsonResult!.Value!.Fields.Should().NotBeNullOrEmpty();
        jsonResult.Value.Fields.Should().Contain(f => f.Type == "esriFieldTypeOID");
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfoAsync_NonMultidimensionalLayer_HasMultidimensionsFalse()
    {
        SetupSuccessfulMetadata();
        // Builder returns null (no multidimensional coverage) by default.

        var context = CreateImageServerContext();
        var result = await _handler.GetServiceInfoAsync(context, 1);

        var jsonResult = result as JsonHttpResult<ImageServerServiceInfo>;
        jsonResult.Should().NotBeNull();
        jsonResult!.Value!.HasMultidimensions.Should().BeFalse();
        jsonResult.Value.MultidimensionalInfo.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfoAsync_MultidimensionalLayer_HasMultidimensionsTrueWithInfo()
    {
        SetupSuccessfulMetadata();
        _multidimensionalInfoBuilder
            .BuildAsync(1, Arg.Any<CancellationToken>())
            .Returns(new ImageServerMultidimensionalInfo
            {
                Variables =
                [
                    new ImageServerMultidimensionalVariable
                    {
                        Name = "temperature",
                        Unit = "K",
                        Dimensions =
                        [
                            new ImageServerMultidimensionalDimension
                            {
                                Name = "StdTime",
                                Unit = "ISO8601",
                                DimensionSize = 12
                            }
                        ]
                    }
                ]
            });

        var context = CreateImageServerContext();
        var result = await _handler.GetServiceInfoAsync(context, 1);

        var jsonResult = result as JsonHttpResult<ImageServerServiceInfo>;
        jsonResult.Should().NotBeNull();
        jsonResult!.Value!.HasMultidimensions.Should().BeTrue();
        jsonResult.Value.MultidimensionalInfo.Should().NotBeNull();
        jsonResult.Value.MultidimensionalInfo!.Variables.Should().ContainSingle()
            .Which.Name.Should().Be("temperature");
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfoAsync_ResponseDoesNotAdvertiseUnroutedTilemapContract()
    {
        SetupSuccessfulMetadata();

        var context = CreateImageServerContext();
        var result = await _handler.GetServiceInfoAsync(context, 1);

        var jsonResult = result as JsonHttpResult<ImageServerServiceInfo>;
        jsonResult.Should().NotBeNull();
        jsonResult!.Value!.Capabilities.Should().NotContain("Tilemap");
        jsonResult.Value.SingleFusedMapCache.Should().BeFalse();
        jsonResult.Value.CacheType.Should().BeNull();
        jsonResult.Value.TileInfo.Should().BeNull();
        jsonResult.Value.MaxImageWidth.Should().Be(4096);
        jsonResult.Value.MaxImageHeight.Should().Be(4096);
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
    public async Task GetServiceInfoAsync_PopulatesFullExtentInitialExtentAndStorageInfo()
    {
        // Regression for #1456: the native .NET ImageServiceRaster.LoadAsync reads
        // fullExtent/initialExtent and storageInfo (blockWidth/blockHeight) and fails
        // "Failed to read configuration data" when they are null. The service is a
        // dynamic (non-tiled) image service, so it must not advertise a tile cache
        // (singleFusedMapCache=false, tileInfo=null) and never trigger a conf.json fetch.
        SetupSuccessfulMetadata();

        var context = CreateImageServerContext();
        var result = await _handler.GetServiceInfoAsync(context, 1);

        var jsonResult = result as JsonHttpResult<ImageServerServiceInfo>;
        jsonResult.Should().NotBeNull();
        var info = jsonResult!.Value!;

        info.FullExtent.Should().NotBeNull();
        info.FullExtent!.XMin.Should().Be(-180);
        info.FullExtent.YMax.Should().Be(90);
        info.FullExtent.SpatialReference.Wkid.Should().Be(4326);

        info.InitialExtent.Should().NotBeNull();
        info.InitialExtent!.XMin.Should().Be(-180);
        info.InitialExtent.YMax.Should().Be(90);

        info.StorageInfo.Should().NotBeNull();
        info.StorageInfo!.BlockWidth.Should().BeGreaterThan(0);
        info.StorageInfo.BlockHeight.Should().BeGreaterThan(0);

        // #1456: blockWidth/blockHeight must ALSO be surfaced at the metadata root, not
        // only nested under storageInfo. The ArcGIS Maps SDK for .NET ImageServiceRaster
        // reads them from the root, and the root values must match the storageInfo values.
        info.BlockWidth.Should().Be(info.StorageInfo.BlockWidth);
        info.BlockHeight.Should().Be(info.StorageInfo.BlockHeight);

        // Non-tiled service: no fused cache, so the SDK never requests conf.json.
        info.SingleFusedMapCache.Should().BeFalse();
        info.TileInfo.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceConfAsync_ReturnsDynamicStorageDescriptor()
    {
        // The ArcGIS Maps SDK for .NET native runtime probes /ImageServer/conf.json
        // when loading an ImageServiceRaster; a 404 is reported as "could not read the
        // ImageServer conf". The dynamic (non-cached) service must return a well-formed
        // descriptor that advertises no fused tile cache and carries the pixel-block
        // storageInfo, extent, and spatial reference the runtime needs.
        SetupSuccessfulMetadata();

        var context = CreateImageServerContext();
        var result = await _handler.GetServiceConfAsync(context, 1);

        var jsonResult = result as JsonHttpResult<ImageServerConfInfo>;
        jsonResult.Should().NotBeNull();
        var conf = jsonResult!.Value!;

        conf.SingleFusedMapCache.Should().BeFalse();
        conf.TileInfo.Should().BeNull();

        conf.StorageInfo.Should().NotBeNull();
        conf.StorageInfo.BlockWidth.Should().BeGreaterThan(0);
        conf.StorageInfo.BlockHeight.Should().BeGreaterThan(0);

        // Block dimensions are mirrored at the root for the native runtime.
        conf.BlockWidth.Should().Be(conf.StorageInfo.BlockWidth);
        conf.BlockHeight.Should().Be(conf.StorageInfo.BlockHeight);

        conf.FullExtent.Should().NotBeNull();
        conf.FullExtent.XMin.Should().Be(-180);
        conf.FullExtent.YMax.Should().Be(90);
        conf.SpatialReference.Wkid.Should().Be(4326);
        conf.BandCount.Should().BeGreaterThan(0);
        conf.PixelType.Should().NotBeNullOrWhiteSpace();
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceConfAsync_LayerNotFound_ReturnsNotFound()
    {
        var context = CreateImageServerContext();
        var result = await _handler.GetServiceConfAsync(context, 99);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceConfAsync_NoRasters_ReturnsNotFound()
    {
        _rasterStore.ListRastersAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<RasterInfo>());

        var context = CreateImageServerContext();
        var result = await _handler.GetServiceConfAsync(context, 1);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
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
