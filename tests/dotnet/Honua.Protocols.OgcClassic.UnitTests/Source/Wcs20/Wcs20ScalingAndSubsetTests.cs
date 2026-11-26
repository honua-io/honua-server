// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Protocols.Ogc.Classic.Wcs20;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wcs20;

public sealed class Wcs20ScalingAndSubsetTests
{
    private static readonly CrsDefinition Crs84 = new("CRS:84", 4326, AxisOrder.EastNorth, true);

    [UnitTest]
    public void ScaleFactor_FollowsOgc12039ResolutionDivisorSemantics()
    {
        Wcs20Handler.TryResolveScaling(Query("SCALEFACTOR", "2"), 64, 32, out var downW, out var downH, out _).Should().BeTrue();
        downW.Should().Be(32);
        downH.Should().Be(16);

        Wcs20Handler.TryResolveScaling(Query("SCALEFACTOR", "0.5"), 64, 32, out var upW, out var upH, out _).Should().BeTrue();
        upW.Should().Be(128);
        upH.Should().Be(64);
    }

    [UnitTest]
    public void ScaleAxes_FollowsOgc12039PerAxisDivisorSemantics()
    {
        var query = Query("SCALEAXES", "x(2),y(0.5)");

        Wcs20Handler.TryResolveScaling(query, 64, 64, out var width, out var height, out _).Should().BeTrue();

        width.Should().Be(32);
        height.Should().Be(128);
    }

    [UnitTest]
    public void ScaleExtent_WithEqualBounds_SelectsOneCell()
    {
        var query = Query("SCALEEXTENT", "x(5,5),y(0,9)");

        Wcs20Handler.TryResolveScaling(query, 64, 64, out var width, out var height, out _).Should().BeTrue();

        width.Should().Be(1);
        height.Should().Be(10);
    }

    [UnitTest]
    public void ScaleExtent_WithHighBelowLow_IsInvalidExtent()
    {
        var query = Query("SCALEEXTENT", "x(6,5),y(0,9)");

        Wcs20Handler.TryResolveScaling(query, 64, 64, out _, out _, out var error).Should().BeFalse();

        error.ExceptionCode.Should().Be(Wcs20Utilities.ExceptionCodes.InvalidExtent);
    }

    [UnitTest]
    public void Subset_WithStarLimits_ExtendsToTheCoverageEdge()
    {
        var subsets = new StringValues(["Long(*,-122.4)", "Lat(37.75,*)"]);

        Wcs20Handler.TryParseSubsetEnvelope(subsets, CreateRaster(), Crs84, out var envelope, out _).Should().BeTrue();

        envelope.MinX.Should().BeApproximately(-122.5, 1e-9);
        envelope.MaxX.Should().BeApproximately(-122.4, 1e-9);
        envelope.MinY.Should().BeApproximately(37.75, 1e-9);
        envelope.MaxY.Should().BeApproximately(37.84, 1e-9);
    }

    [UnitTheory]
    [InlineData("Long(NaN,*)")]
    [InlineData("Long(*)")]
    [InlineData("Long(-122.4,-Infinity)")]
    public void Subset_WithInvalidLimit_IsInvalidSubsetting(string subset)
    {
        var subsets = new StringValues([subset, "Lat(37.75,37.8)"]);

        Wcs20Handler.TryParseSubsetEnvelope(subsets, CreateRaster(), Crs84, out _, out var error).Should().BeFalse();

        error.ExceptionCode.Should().Be(Wcs20Utilities.ExceptionCodes.InvalidSubsetting);
    }

    [UnitTest]
    public void Subset_WithRepeatedAxisAndMissingLimit_IsInvalidAxisLabel()
    {
        var subsets = new StringValues(["Long(-122.5,-122.4)", "Lon(,-122.4)"]);

        Wcs20Handler.TryParseSubsetEnvelope(subsets, CreateRaster(), Crs84, out _, out var error).Should().BeFalse();

        error.ExceptionCode.Should().Be(Wcs20Utilities.ExceptionCodes.InvalidAxisLabel);
    }

    [UnitTest]
    public void Subset_WithStarLimitInNonNativeCrs_IsInvalidSubsetting()
    {
        var webMercator = new CrsDefinition("EPSG:3857", 3857, AxisOrder.EastNorth, false);
        var subsets = new StringValues(["E(*,-13620000)", "N(4540000,4550000)"]);

        Wcs20Handler.TryParseSubsetEnvelope(subsets, CreateRaster(), webMercator, out _, out var error).Should().BeFalse();

        error.ExceptionCode.Should().Be(Wcs20Utilities.ExceptionCodes.InvalidSubsetting);
    }

    private static RasterInfo CreateRaster()
        => new()
        {
            Id = 1,
            LayerId = 0,
            Name = "star-limit-raster",
            Width = 64,
            Height = 64,
            BandCount = 1,
            Srid = 4326,
            PixelType = "32BF",
            Extent = new RasterExtent { XMin = -122.5, YMin = 37.7, XMax = -122.35, YMax = 37.84, Srid = 4326 }
        };

    private static QueryCollection Query(string key, string value)
        => new(new Dictionary<string, StringValues> { [key] = value });
}
