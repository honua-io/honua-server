// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
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
using Microsoft.Extensions.Primitives;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

// getSamples and keyProperties on a layer whose imagery is only a registered cloud COG read the
// COG instead of answering empty or "No rasters found".
[Protocol(TestProtocols.ImageServer)]
public sealed class ImageServerCogSamplesTests
{
    // The bounded-roster COG: 512 x 512 at 38.2185 m with 256-pixel tiles, native zoom 12.
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

    public ImageServerCogSamplesTests()
    {
        _rasterStore.QueryRastersAsync(default, default, default).ReturnsForAnyArgs(Array.Empty<RasterInfo>());
        _rasterStore.ListRastersAsync(default, default).ReturnsForAnyArgs(Array.Empty<RasterInfo>());
        _cogStore.ListByLayerAsync(1, Arg.Any<CancellationToken>()).Returns(new[] { Registration() });
        var samples = new byte[256 * 256];
        samples[(20 * 256) + 10] = 42;
        samples[(30 * 256) + 40] = 7;
        _tileResolver.GetTileAsync(Arg.Any<CogRegistration>(), 12, 1582, 654, RasterFormat.Raw, Arg.Any<CancellationToken>())
            .Returns(new RasterResult { Data = samples, ContentType = "application/octet-stream", Width = 256, Height = 256 });
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task GetSamplesAsync_CogOnlyLayer_SamplesEveryPointFromOneRegistrationLookup()
    {
        var handler = SamplesHandler();
        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["geometry"] = $"{{\"points\":[[{Point(20, 10)}],[{Point(30, 40)}]],\"spatialReference\":{{\"wkid\":102100}}}}",
            ["geometryType"] = "esriGeometryMultipoint",
            ["f"] = "json",
        };

        var result = await handler.GetSamplesAsync(CreateContext(), 1, values, CancellationToken.None);

        var response = result.Should().BeOfType<JsonHttpResult<GetSamplesResponse>>().Which.Value!;
        response.Samples.Select(sample => sample.Value).Should().Equal("42", "7");
        response.Samples.Should().AllSatisfy(sample => sample.RasterId.Should().Be(7));
        await _cogStore.ReceivedWithAnyArgs(1).ListByLayerAsync(default, default);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task GetSamplesAsync_CogOnlyLayerWithGeographicPoint_AnswersNotImplemented()
    {
        var context = CreateContext();
        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["geometry"] = "{\"x\":-122.4,\"y\":37.8,\"spatialReference\":{\"wkid\":4326}}",
            ["f"] = "json",
        };

        var result = await SamplesHandler().GetSamplesAsync(context, 1, values, CancellationToken.None);

        await AssertGeoServicesErrorAsync(context, result, StatusCodes.Status501NotImplemented);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task GetSamplesAsync_PointAnsweredByRasterData_NeverReachesTheCogStore()
    {
        _rasterStore.QueryRastersAsync(default, default, default).ReturnsForAnyArgs([ImageServerTestFixtures.CreateSmallRasterInfo(bandCount: 1)]);
        _rasterStore.IdentifyAsync(default, default, default, default).ReturnsForAnyArgs(new PixelValueResult
        {
            X = 0,
            Y = 0,
            BandValues = new Dictionary<int, object?> { [1] = 5d },
            HasData = true,
        });
        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase) { ["geometry"] = "{\"x\":0,\"y\":0}", ["f"] = "json" };

        var result = await SamplesHandler().GetSamplesAsync(CreateContext(), 1, values, CancellationToken.None);

        result.Should().BeOfType<JsonHttpResult<GetSamplesResponse>>();
        await _cogStore.DidNotReceiveWithAnyArgs().ListByLayerAsync(default, default);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task GetKeyPropertiesAsync_CogOnlyLayer_DescribesTheCogBands()
    {
        var result = await KeyPropertiesHandler().GetKeyPropertiesAsync(CreateContext(), 1, CancellationToken.None);

        var response = result.Should().BeOfType<JsonHttpResult<KeyPropertiesResponse>>().Which.Value!;
        response.BandProperties.Should().ContainSingle();
        response.HighCellSize.Should().BeApproximately(PixelSize, 1e-6);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task GetKeyPropertiesAsync_CogOnlyLayerWithoutEntitlement_AnswersPaymentRequiredLikeTiles()
    {
        var licensing = Substitute.For<ILicenseEntitlementService>();
        licensing.CheckEntitlement(ImageServerCogRasters.Entitlement).Returns(new LicenseEntitlementDecision(
            ImageServerCogRasters.Entitlement, false, default, default, RequiredEdition: null, UpgradeMessage: "not active"));
        var context = CreateContext(services => services.AddSingleton(licensing));

        var result = await KeyPropertiesHandler().GetKeyPropertiesAsync(context, 1, CancellationToken.None);

        await AssertGeoServicesErrorAsync(context, result, StatusCodes.Status402PaymentRequired);
    }

    private ImageServerSamplesHandler SamplesHandler()
        => new(ImageServerTestFixtures.BuildGraphWithLayer(1), _rasterStore, Substitute.For<IZarrPointSliceReader>(), NullLogger<ImageServerSamplesHandler>.Instance);

    private ImageServerKeyPropertiesHandler KeyPropertiesHandler()
        => new(ImageServerTestFixtures.BuildGraphWithLayer(1), _rasterStore, NullLogger<ImageServerKeyPropertiesHandler>.Instance);

    private static string Point(int row, int column)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{CogExtent.XMin + ((column + 0.5) * PixelSize)},{CogExtent.YMax - ((row + 0.5) * PixelSize)}");

    private static CogRegistration Registration() => new()
    {
        Id = 7,
        LayerId = 1,
        Name = "roster cog",
        Provider = CloudStorageProvider.AwsS3,
        Bucket = "roster-fixtures",
        ObjectKey = "cog/roster-3857.tif",
        Metadata = new CogMetadata(512, 512, 1, "uint8", 3857, "DEFLATE", 256, 256,
            [new(0, 512, 512, 0, [], []), new(1, 256, 256, 0, [], [])], CogExtent, NoData: "0"),
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    private DefaultHttpContext CreateContext(Action<IServiceCollection>? configure = null)
        => ImageServerTestFixtures.CreateContext("/rest/services/1/ImageServer", services =>
        {
            services.AddSingleton(_cogStore);
            services.AddSingleton(_tileResolver);
            configure?.Invoke(services);
        });
}
