// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer.Handlers;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

// identify on a layer whose imagery is only a registered cloud COG reads the pixel from the
// finest grid tile instead of answering NoData everywhere.
[Protocol(TestProtocols.ImageServer)]
public sealed class ImageServerCogIdentifyTests
{
    // The bounded-roster COG: 512 x 512 at 38.2185 m with 256-pixel tiles, so its native grid is
    // zoom 12 and the north-west tile is 654/1582.
    private static readonly RasterExtent CogExtent = new()
    {
        XMin = -13638811.83098057,
        YMin = 4539747.983913188,
        XMax = -13619243.951739565,
        YMax = 4559315.863154193,
    };

    private const double PixelSize = 19567.879241005 / 512;

    private readonly IRasterStore _rasterStore = Substitute.For<IRasterStore>();
    private readonly ICogStore _cogStore = Substitute.For<ICogStore>();
    private readonly ICogTileResolver _tileResolver = Substitute.For<ICogTileResolver>();
    private readonly ImageServerIdentifyHandler _handler;

    public ImageServerCogIdentifyTests()
    {
        _rasterStore.QueryRastersAsync(default, default, default).ReturnsForAnyArgs(Array.Empty<RasterInfo>());
        _cogStore.ListByLayerAsync(1, Arg.Any<CancellationToken>()).Returns(new[] { Registration() });
        _handler = new ImageServerIdentifyHandler(
            ImageServerTestFixtures.BuildGraphWithLayer(1),
            _rasterStore,
            Substitute.For<IZarrPointSliceReader>(),
            NullLogger<ImageServerIdentifyHandler>.Instance);
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_CogOnlyLayer_ReportsTheBandsOfThePixelUnderThePoint()
    {
        TileWithPixel(row: 20, column: 10, [11, 22, 33]);

        using var json = await IdentifyJsonAsync(PointInPixel(row: 20, column: 10));

        json.RootElement.GetProperty("value").GetString().Should().Be("11, 22, 33");
        json.RootElement.GetProperty("objectId").GetInt64().Should().Be(7);
        json.RootElement.GetProperty("properties").GetProperty("Band_2").GetDouble().Should().Be(22);
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_CogPixelHoldingNoData_ReportsNoData()
    {
        TileWithPixel(row: 20, column: 10, [0, 0, 0]);

        using var json = await IdentifyJsonAsync(PointInPixel(row: 20, column: 10));

        json.RootElement.GetProperty("value").GetString().Should().Be("NoData");
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_PointOutsideTheCog_AnswersNoDataWithoutReadingTiles()
    {
        using var json = await IdentifyJsonAsync(FormattableString.Invariant($"{CogExtent.XMax + 1000},{CogExtent.YMax}"));

        json.RootElement.GetProperty("value").GetString().Should().Be("NoData");
        await _tileResolver.DidNotReceiveWithAnyArgs().GetTileAsync(default!, default, default, default, default, default);
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_CogOnlyLayerWithoutEntitlement_AnswersPaymentRequiredLikeTiles()
    {
        var licensing = Substitute.For<ILicenseEntitlementService>();
        licensing.CheckEntitlement(ImageServerCogRasters.Entitlement).Returns(new LicenseEntitlementDecision(
            ImageServerCogRasters.Entitlement, false, default, default, RequiredEdition: null, UpgradeMessage: "not active"));
        var context = CreateContext(services => services.AddSingleton(licensing));

        var result = await _handler.IdentifyAsync(context, 1, Request(PointInPixel(row: 20, column: 10)));

        await AssertGeoServicesErrorAsync(context, result, StatusCodes.Status402PaymentRequired);
    }

    [UnitTest]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_CogOnlyLayerWithGeographicPoint_AnswersNotImplemented()
    {
        var context = CreateContext();
        var request = new IdentifyRequest { Geometry = "-122.4,37.8", GeometryType = "esriGeometryPoint", Sr = "4326", F = "json" };

        var result = await _handler.IdentifyAsync(context, 1, request);

        await AssertGeoServicesErrorAsync(context, result, StatusCodes.Status501NotImplemented);
        await _tileResolver.DidNotReceiveWithAnyArgs().GetTileAsync(default!, default, default, default, default, default);
    }

    [UnitTest]
    public void ReadBands_BigEndianFloat32_DecodesInFileByteOrder()
    {
        var metadata = CogMetadata() with { BandCount = 1, PixelType = "float32", BitsPerSample = 32, IsLittleEndian = false };
        byte[] samples = [0, 0, 0, 0, 0x41, 0x48, 0, 0];

        ImageServerCogIdentify.ReadBands(samples, metadata, pixelIndex: 1).Should().Equal(12.5);
    }

    [UnitTheory]
    [InlineData(-9999.9f)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(float.NaN)]
    [Operation(Operations.Identify)]
    public async Task IdentifyAsync_Float32NoDataOrNonFiniteSample_ReportsNoData(float sampleValue)
    {
        var floatMeta = CogMetadata() with { BandCount = 1, PixelType = "float32", BitsPerSample = 32, NoData = "-9999.9" };
        _cogStore.ListByLayerAsync(1, Arg.Any<CancellationToken>()).Returns(new[] { Registration() with { Metadata = floatMeta } });
        var samples = new byte[256 * 256 * 4];
        BitConverter.GetBytes(sampleValue).CopyTo(samples, ((20 * 256) + 10) * 4);
        _tileResolver.GetTileAsync(Arg.Any<CogRegistration>(), 12, 1582, 654, RasterFormat.Raw, Arg.Any<CancellationToken>())
            .Returns(new RasterResult { Data = samples, ContentType = "application/octet-stream", Width = 256, Height = 256 });

        using var json = await IdentifyJsonAsync(PointInPixel(row: 20, column: 10));

        json.RootElement.GetProperty("value").GetString().Should().Be("NoData");
    }

    private void TileWithPixel(int row, int column, byte[] bands)
    {
        var samples = new byte[256 * 256 * 3];
        bands.CopyTo(samples, ((row * 256) + column) * 3);
        _tileResolver.GetTileAsync(Arg.Any<CogRegistration>(), 12, 1582, 654, RasterFormat.Raw, Arg.Any<CancellationToken>())
            .Returns(new RasterResult { Data = samples, ContentType = "application/octet-stream", Width = 256, Height = 256 });
    }

    private static string PointInPixel(int row, int column)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{CogExtent.XMin + ((column + 0.5) * PixelSize)},{CogExtent.YMax - ((row + 0.5) * PixelSize)}");

    private async Task<JsonDocument> IdentifyJsonAsync(string geometry)
    {
        var context = CreateContext();
        var result = await _handler.IdentifyAsync(context, 1, Request(geometry));
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.Body.Position = 0;
        return await JsonDocument.ParseAsync(context.Response.Body);
    }

    private static IdentifyRequest Request(string geometry) => new()
    {
        Geometry = geometry,
        GeometryType = "esriGeometryPoint",
        Sr = "102100",
        F = "json",
    };

    private static CogMetadata CogMetadata()
        => new(512, 512, 3, "uint8", 3857, "DEFLATE", 256, 256, [new(0, 512, 512, 0, [], []), new(1, 256, 256, 0, [], [])], CogExtent, NoData: "0");

    private static CogRegistration Registration() => new()
    {
        Id = 7,
        LayerId = 1,
        Name = "roster cog",
        Provider = CloudStorageProvider.AwsS3,
        Bucket = "roster-fixtures",
        ObjectKey = "cog/roster-3857.tif",
        Metadata = CogMetadata(),
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    private DefaultHttpContext CreateContext(Action<IServiceCollection>? configure = null)
    {
        var masks = Substitute.For<IFieldMaskSource>();
        masks.ResolveAsync(Arg.Any<MetadataV2Object>(), Arg.Any<CancellationToken>()).Returns(ImmutableArray<string>.Empty);
        return ImageServerTestFixtures.CreateContext("/rest/services/1/ImageServer/identify", services =>
        {
            services.AddSingleton(masks);
            services.AddSingleton(_cogStore);
            services.AddSingleton(_tileResolver);
            configure?.Invoke(services);
        });
    }
}
