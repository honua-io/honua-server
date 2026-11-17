// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer;
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

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

// A layer whose imagery is only a registered cloud COG: the tile route serves it, so the service
// document must describe the scanned COG grid instead of answering "No rasters found".
[Protocol(TestProtocols.ImageServer)]
public sealed class ImageServerCogServiceInfoTests
{
    // The GDAL GoogleMapsCompatible zoom-12 COG of the bounded-roster fixture: 512 x 512 at 38.2185 m.
    private static readonly RasterExtent CogExtent = new()
    {
        XMin = -13638811.83098057,
        YMin = 4539747.983913188,
        XMax = -13619243.951739565,
        YMax = 4559315.863154193,
    };

    private readonly IRasterStore _rasterStore = Substitute.For<IRasterStore>();
    private readonly ICogStore _cogStore = Substitute.For<ICogStore>();
    private readonly ImageServerMetadataHandler _handler;

    public ImageServerCogServiceInfoTests()
    {
        _rasterStore.ListRastersAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<RasterInfo>());
        var multidimensional = Substitute.For<IImageServerMultidimensionalInfoBuilder>();
        multidimensional.BuildAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns((ImageServerMultidimensionalInfo?)null);
        _handler = new ImageServerMetadataHandler(ImageServerTestFixtures.BuildGraphWithLayer(1), _rasterStore,
            multidimensional, NullLogger<ImageServerMetadataHandler>.Instance);
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfoAsync_CogOnlyLayer_DescribesTheScannedCogGrid()
    {
        _cogStore.ListByLayerAsync(1, Arg.Any<CancellationToken>()).Returns(new[] { Registration(CogMetadata()) });

        var result = await _handler.GetServiceInfoAsync(CreateContext(), 1);

        var info = result.Should().BeOfType<JsonHttpResult<ImageServerServiceInfo>>().Which.Value!;
        info.Extent.XMin.Should().Be(CogExtent.XMin);
        info.Extent.YMax.Should().Be(CogExtent.YMax);
        info.SpatialReference.Wkid.Should().Be(3857);
        info.BandCount.Should().Be(1);
        info.PixelSizeX.Should().BeApproximately(38.2185, 1e-3);
        info.MinValues.Should().BeEmpty("COG statistics are not computed from raster_data");
        await _rasterStore.DidNotReceiveWithAnyArgs().GetStatisticsAsync(default, default, default, default, default);
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfoAsync_UnscannedCogRegistration_StaysNotFound()
    {
        _cogStore.ListByLayerAsync(1, Arg.Any<CancellationToken>()).Returns(new[] { Registration(metadata: null) });

        var context = CreateContext();
        var result = await _handler.GetServiceInfoAsync(context, 1);

        await AssertGeoServicesErrorAsync(context, result, StatusCodes.Status404NotFound);
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfoAsync_PostgisRastersPresent_DoesNotConsultCogs()
    {
        _rasterStore.ListRastersAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { ImageServerTestFixtures.CreateSmallRasterInfo(bandCount: 1) });

        var result = await _handler.GetServiceInfoAsync(CreateContext(), 1);

        result.Should().BeOfType<JsonHttpResult<ImageServerServiceInfo>>();
        await _cogStore.DidNotReceiveWithAnyArgs().ListByLayerAsync(default, default);
    }

    private static CogMetadata CogMetadata()
        => new(512, 512, 1, "uint8", 3857, "DEFLATE", 256, 256, [], CogExtent, NoData: "0");

    private static CogRegistration Registration(CogMetadata? metadata) => new()
    {
        Id = 7,
        LayerId = 1,
        Name = "roster cog",
        Provider = CloudStorageProvider.AwsS3,
        Bucket = "roster-fixtures",
        ObjectKey = "cog/roster-3857.tif",
        Metadata = metadata,
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    private DefaultHttpContext CreateContext()
        => ImageServerTestFixtures.CreateContext("/rest/services/1/ImageServer", services =>
        {
            services.AddSingleton(_rasterStore);
            services.AddSingleton(_cogStore);
            services.AddOptions();
            services.Configure<ImageServerTileMetadataOptions>(_ => { });
        });
}
