// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.Ogc.Classic.Wcs20;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wcs20;

/// <summary>
/// Unit tests for the static WCS 2.0 request-CRS resolver. These exercise the
/// advertise/validate agreement (OGC 11-053r1) without a database by substituting
/// the transformable CRS set directly, mirroring the bounded set the handler builds
/// from the CRS registry at runtime.
/// </summary>
public sealed class Wcs20RequestCrsValidationTests
{
    // Native 4326 coverage; supported set matches the default {native, 4326, 3857}.
    private static readonly IReadOnlySet<int> SupportedSrids = new HashSet<int> { 4326, 3857 };

    [Fact]
    public void TryResolveRequestCrs_WithSupportedOutputCrs_Accepts()
    {
        var query = BuildQuery(("OUTPUTCRS", "EPSG:3857"));

        var resolved = Wcs20Handler.TryResolveRequestCrs(
            query, CreateRaster(4326), SupportedSrids, out _, out var outputSrid, out _);

        resolved.Should().BeTrue();
        outputSrid.Should().Be(3857);
    }

    [Fact]
    public void TryResolveRequestCrs_WithUnsupportedOutputCrs_RejectsWithOutputCrsNotSupported()
    {
        var query = BuildQuery(("OUTPUTCRS", "EPSG:99999"));

        var resolved = Wcs20Handler.TryResolveRequestCrs(
            query, CreateRaster(4326), SupportedSrids, out _, out _, out var error);

        resolved.Should().BeFalse();
        error.ExceptionCode.Should().Be("OutputCrs-NotSupported");
        error.Locator.Should().Be("OUTPUTCRS");
    }

    [Fact]
    public void TryResolveRequestCrs_WithMalformedOutputCrs_RejectsWithInvalidParameterValue()
    {
        var query = BuildQuery(("OUTPUTCRS", "not-a-crs"));

        var resolved = Wcs20Handler.TryResolveRequestCrs(
            query, CreateRaster(4326), SupportedSrids, out _, out _, out var error);

        resolved.Should().BeFalse();
        error.ExceptionCode.Should().Be("InvalidParameterValue");
        error.Locator.Should().Be("OUTPUTCRS");
    }

    [Fact]
    public void TryResolveRequestCrs_WithUnsupportedSubsettingCrs_RejectsWithSubsettingCrsNotSupported()
    {
        var query = BuildQuery(("SUBSETTINGCRS", "EPSG:99999"));

        var resolved = Wcs20Handler.TryResolveRequestCrs(
            query, CreateRaster(4326), SupportedSrids, out _, out _, out var error);

        resolved.Should().BeFalse();
        error.ExceptionCode.Should().Be("SubsettingCrs-NotSupported");
        error.Locator.Should().Be("SUBSETTINGCRS");
    }

    [Fact]
    public void TryResolveRequestCrs_WithUnsupportedBboxCrs_RejectsWithBboxCrsLocator()
    {
        var query = BuildQuery(("BBOXCRS", "EPSG:99999"));

        var resolved = Wcs20Handler.TryResolveRequestCrs(
            query, CreateRaster(4326), SupportedSrids, out _, out _, out var error);

        resolved.Should().BeFalse();
        error.ExceptionCode.Should().Be("SubsettingCrs-NotSupported");
        error.Locator.Should().Be("BBOXCRS");
    }

    [Fact]
    public void TryResolveRequestCrs_WithNoCrsParameters_AcceptsNativeDefault()
    {
        var query = BuildQuery();

        var resolved = Wcs20Handler.TryResolveRequestCrs(
            query, CreateRaster(4326), SupportedSrids, out var subsettingCrs, out var outputSrid, out _);

        resolved.Should().BeTrue();
        outputSrid.Should().BeNull();
        subsettingCrs.Srid.Should().Be(4326);
    }

    [Fact]
    public void TryResolveRequestCrs_WithSupportedSubsettingCrs_Accepts()
    {
        var query = BuildQuery(("SUBSETTINGCRS", "EPSG:3857"));

        var resolved = Wcs20Handler.TryResolveRequestCrs(
            query, CreateRaster(4326), SupportedSrids, out var subsettingCrs, out _, out _);

        resolved.Should().BeTrue();
        subsettingCrs.Srid.Should().Be(3857);
    }

    private static QueryCollection BuildQuery(params (string Key, string Value)[] parameters)
    {
        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in parameters)
        {
            values[key] = value;
        }

        return new QueryCollection(values);
    }

    private static RasterInfo CreateRaster(int srid)
        => new()
        {
            Id = 1,
            LayerId = 1,
            Name = "unit-test-raster",
            Width = 64,
            Height = 64,
            BandCount = 1,
            Srid = srid,
            PixelType = "32BF",
            Extent = new RasterExtent
            {
                XMin = -122.5,
                YMin = 37.7,
                XMax = -122.35,
                YMax = 37.84,
                Srid = srid
            }
        };
}
