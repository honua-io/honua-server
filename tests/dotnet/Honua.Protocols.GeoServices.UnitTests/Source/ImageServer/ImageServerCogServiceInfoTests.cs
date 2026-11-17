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

// A layer served only by registered cloud COGs is described from the scanned COG metadata
// instead of answering "No rasters found for layer.".
[Protocol(TestProtocols.ImageServer)]
public class ImageServerCogServiceInfoTests
{
    private readonly IRasterStore _rasterStore = Substitute.For<IRasterStore>();
    private readonly ICogStore _cogStore = Substitute.For<ICogStore>();
    private readonly ImageServerMetadataHandler _handler;

    public ImageServerCogServiceInfoTests()
    {
        _rasterStore.ListRastersAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<RasterInfo>());
        var multidimensional = Substitute.For<IImageServerMultidimensionalInfoBuilder>();
        multidimensional.BuildAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((ImageServerMultidimensionalInfo?)null);
        _handler = new ImageServerMetadataHandler(
            ImageServerTestFixtures.BuildGraphWithLayer(1),
            _rasterStore,
            multidimensional,
            NullLogger<ImageServerMetadataHandler>.Instance);
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfoAsync_CogOnlyLayer_DescribesScannedCog()
    {
        _cogStore.ListByLayerAsync(1, Arg.Any<CancellationToken>()).Returns([CreateRegistration(CreateMetadata())]);

        var result = await _handler.GetServiceInfoAsync(CreateContext(_cogStore), 1);

        var info = result.Should().BeOfType<JsonHttpResult<ImageServerServiceInfo>>().Which.Value!;
        info.Extent.XMin.Should().Be(-13638811.83098057);
        info.Extent.YMax.Should().Be(4544761.33520707);
        info.SpatialReference.Wkid.Should().Be(3857);
        info.BandCount.Should().Be(1);
        info.PixelType.Should().Be("U8");
        info.PixelSizeX.Should().BeApproximately(9.554944693931247, 1e-6);
        info.MinValues.Should().BeEmpty("COG band statistics are not scanned");
        await _rasterStore.DidNotReceiveWithAnyArgs()
            .GetStatisticsAsync(default, default, default, default, default);
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfoAsync_UnscannedCogRegistration_ReturnsNotFound()
    {
        _cogStore.ListByLayerAsync(1, Arg.Any<CancellationToken>()).Returns([CreateRegistration(metadata: null)]);

        var result = await _handler.GetServiceInfoAsync(CreateContext(_cogStore), 1);

        result.Should().NotBeOfType<JsonHttpResult<ImageServerServiceInfo>>();
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetServiceInfoAsync_NoCogStore_ReturnsNotFound()
    {
        var result = await _handler.GetServiceInfoAsync(CreateContext(cogStore: null), 1);

        result.Should().NotBeOfType<JsonHttpResult<ImageServerServiceInfo>>();
    }

    [UnitTheory]
    [InlineData("uint8", "8BUI")]
    [InlineData("int16", "16BSI")]
    [InlineData("uint32", "32BUI")]
    [InlineData("float32", "32BF")]
    [InlineData("float64", "64BF")]
    public void ToPostgisPixelType_MapsCogSampleTypes(string cogPixelType, string expected)
        => ImageServerCogRasters.ToPostgisPixelType(cogPixelType).Should().Be(expected);

    private DefaultHttpContext CreateContext(ICogStore? cogStore)
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(_rasterStore);
        if (cogStore is not null)
        {
            services.AddSingleton(cogStore);
        }

        services.AddOptions();
        services.Configure<ImageServerTileMetadataOptions>(_ => { });
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Request.Path = "/rest/services/1/ImageServer";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static CogRegistration CreateRegistration(CogMetadata? metadata) => new()
    {
        Id = 9,
        LayerId = 1,
        Name = "roster cog",
        Provider = CloudStorageProvider.AwsS3,
        Bucket = "bucket",
        ObjectKey = "cog/roster-3857.tif",
        Metadata = metadata,
        CreatedAt = DateTimeOffset.UnixEpoch
    };

    // The z12 GoogleMapsCompatible tile (655, 1583) written as 1024 pixels of 9.55 m.
    private static CogMetadata CreateMetadata() => new(
        Width: 1024,
        Height: 1024,
        BandCount: 1,
        PixelType: "uint8",
        Srid: 3857,
        Compression: "DEFLATE",
        TileWidth: 256,
        TileHeight: 256,
        OverviewLevels: [],
        Extent: new RasterExtent
        {
            XMin = -13638811.83098057,
            YMin = 4534977.067315336,
            XMax = -13629027.567613983,
            YMax = 4544761.33520707,
            Srid = 3857
        });
}
