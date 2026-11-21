// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Protocols.GeoServices.ImageServer.Handlers;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Extensions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

[Protocol(TestProtocols.ImageServer)]
public sealed class ImageServerRequestListLimitsTests
{
    private const int MaxBands = 256;
    private const int MaxRasterIds = 1_000;
    private const int MaxPoints = 1_000;

    [UnitTest]
    public void BandIds_AtTheLimitWithRepeats_AreAccepted()
    {
        var csv = string.Join(',', Enumerable.Repeat(0, MaxBands));

        ImageServerCatalogIdParser.TryParseBandIds(csv, out var bands, out _).Should().BeTrue();
        bands.Should().HaveCount(MaxBands).And.OnlyContain(band => band == 1);
        ImageServerExportHandler.TryParseBandIds("0,0,0", out var rgb, out _).Should().BeTrue();
        rgb.Should().Equal(1, 1, 1);
    }

    [UnitTest]
    public void BandIds_OverTheLimit_AreRefusedOnEveryList()
    {
        var csv = string.Join(',', Enumerable.Range(0, MaxBands + 1));

        ImageServerCatalogIdParser.TryParseBandIds(csv, out _, out var catalogError).Should().BeFalse();
        catalogError.Should().Contain("at most 256");
        ImageServerCatalogIdParser.TryParseBandIds($"[{csv}]", out _, out _).Should().BeFalse();
        ImageServerExportHandler.TryParseBandIds(csv, out _, out var exportError).Should().BeFalse();
        exportError.Should().Contain("at most 256");

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(ExtractBand(csv));
        mapping.Supported.Should().BeFalse();
        mapping.Reason.Should().Contain("at most 256");
    }

    [UnitTest]
    public void ExtractBand_LargestIndex_IsRefusedInsteadOfWrapping()
    {
        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(ExtractBand(int.MaxValue.ToString(CultureInfo.InvariantCulture)));

        mapping.Supported.Should().BeFalse();
        mapping.Bands.Should().BeNull();
    }

    [UnitTest]
    public void RasterIds_AreLimited()
    {
        var atLimit = string.Join(',', Enumerable.Range(1, MaxRasterIds));
        var overLimit = string.Join(',', Enumerable.Range(1, MaxRasterIds + 1));

        ImageServerCatalogIdParser.TryParseRasterIds(atLimit, out var ids, out _).Should().BeTrue();
        ids.Should().HaveCount(MaxRasterIds);
        ImageServerCatalogIdParser.TryParseRasterIds(overLimit, out _, out var error).Should().BeFalse();
        error.Should().Contain("at most 1000");
        ImageServerCatalogIdParser.TryParseRasterIds($"[{overLimit}]", out _, out _).Should().BeFalse();
    }

    [UnitTest]
    public async Task ComputePixelLocation_OverThePointLimit_IsRefusedBeforeAnyTransform()
    {
        var transforms = IdentityTransforms();
        var handler = CreatePixelLocationHandler(transforms);
        var context = ImageServerTestFixtures.CreateContext("/rest/services/1/ImageServer/computePixelLocation");

        var result = await handler.ComputePixelLocationAsync(context, 1, PointValues(MaxPoints + 1), CancellationToken.None);

        await GeoServicesErrorAssertions.AssertGeoServicesErrorAsync(context, result, StatusCodes.Status400BadRequest);
        transforms.ReceivedCalls().Should().BeEmpty();
    }

    [UnitTest]
    public async Task ComputePixelLocation_TransformsThePointsInOneBatch()
    {
        var transforms = IdentityTransforms();
        var handler = CreatePixelLocationHandler(transforms);
        var context = ImageServerTestFixtures.CreateContext("/rest/services/1/ImageServer/computePixelLocation");

        var result = await handler.ComputePixelLocationAsync(context, 1, PointValues(MaxPoints), CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.Body.Position = 0;
        var response = await JsonSerializer.DeserializeAsync(
            context.Response.Body,
            ImageServerJsonContext.Default.ComputePixelLocationResponse);
        response!.Geometries.Should().HaveCount(MaxPoints);
        response.Geometries[0].X.Should().Be(10);
        response.Geometries[0].Y.Should().Be(-20);
        await transforms.Received(1).TransformPointsAsync(
            Arg.Is<double[]>(xs => xs.Length == MaxPoints), Arg.Any<double[]>(), Arg.Is(3857), Arg.Is(4326), Arg.Any<CancellationToken>());
        await transforms.DidNotReceive().TransformPointAsync(
            Arg.Any<double>(), Arg.Any<double>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    private static RasterFunctionDocument ExtractBand(string bandIds)
        => JsonSerializer.Deserialize(
            $$$"""{"rasterFunction":"ExtractBand","rasterFunctionArguments":{"BandIds":[{{{bandIds}}}]}}""",
            ImageServerJsonContext.Default.RasterFunctionDocument)!;

    private static Dictionary<string, StringValues> PointValues(int count)
    {
        var points = string.Join(',', Enumerable.Range(0, count).Select(_ => """{"x":10,"y":20}"""));
        return new Dictionary<string, StringValues>
        {
            ["geometries"] = $$"""{"geometryType":"esriGeometryPoint","geometries":[{{points}}]}""",
            ["spatialReference"] = "3857",
        };
    }

    private static ImageServerCoordinateMetadataHandler CreatePixelLocationHandler(
        ICoordinateTransformService transforms)
    {
        var store = Substitute.For<IRasterStore>();
        store.GetPrimaryRasterInfoAsync(1, Arg.Any<CancellationToken>())
            .Returns(ImageServerTestFixtures.CreateWorldRgbRasterInfo());
        return new ImageServerCoordinateMetadataHandler(
            store,
            Substitute.For<IImageServerCatalogReader>(),
            NullLogger<ImageServerCoordinateMetadataHandler>.Instance,
            transforms);
    }

    private static ICoordinateTransformService IdentityTransforms()
    {
        var transforms = Substitute.For<ICoordinateTransformService>();
#pragma warning disable CA2012 // NSubstitute setup for a ValueTask-returning member.
        transforms.TransformPointsAsync(Arg.Any<double[]>(), Arg.Any<double[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<bool>(true));
#pragma warning restore CA2012
        return transforms;
    }
}
