// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.Ogc.Shared;

#pragma warning disable CA1716 // Mirrors the namespace under test (Honua.Protocols.Ogc.Shared).
namespace Honua.Server.Tests.Features.Protocols.Ogc.Shared;
#pragma warning restore CA1716

public sealed class OgcParameterValidatorTests
{
    // ------------------------------------------------------------------
    // TryParseBbox
    // ------------------------------------------------------------------

    [Fact]
    public void TryParseBbox_Parses2dHappyPath()
    {
        var ok = OgcParameterValidator.TryParseBbox("1,2,3,4", out var bbox, out var error);

        ok.Should().BeTrue();
        error.Should().BeEmpty();
        bbox.MinX.Should().Be(1);
        bbox.MinY.Should().Be(2);
        bbox.MaxX.Should().Be(3);
        bbox.MaxY.Should().Be(4);
        bbox.Is3D.Should().BeFalse();
        bbox.CrsToken.Should().BeNull();
    }

    [Fact]
    public void TryParseBbox_TrimsWhitespaceBetweenTokens()
    {
        var ok = OgcParameterValidator.TryParseBbox("  1 , 2 , 3 , 4 ", out var bbox, out _);
        ok.Should().BeTrue();
        bbox.MinX.Should().Be(1);
        bbox.MaxY.Should().Be(4);
    }

    [Fact]
    public void TryParseBbox_AcceptsNegativeAndDecimals()
    {
        var ok = OgcParameterValidator.TryParseBbox("-122.5,-37.8,-122.4,-37.7", out var bbox, out _);
        ok.Should().BeTrue();
        bbox.MinX.Should().Be(-122.5);
        bbox.MaxY.Should().Be(-37.7);
    }

    [Fact]
    public void TryParseBbox_AcceptsScientificNotation()
    {
        var ok = OgcParameterValidator.TryParseBbox("1e0,2e0,3e0,4e0", out var bbox, out _);
        ok.Should().BeTrue();
        bbox.MaxX.Should().Be(3);
    }

    [Fact]
    public void TryParseBbox_Parses3dHappyPath()
    {
        var ok = OgcParameterValidator.TryParseBbox("1,2,10,3,4,20", out var bbox, out _);
        ok.Should().BeTrue();
        bbox.Is3D.Should().BeTrue();
        bbox.MinZ.Should().Be(10);
        bbox.MaxZ.Should().Be(20);
        bbox.MinX.Should().Be(1);
        bbox.MaxX.Should().Be(3);
    }

    [Fact]
    public void TryParseBbox_Accepts2dWithCrsSuffix()
    {
        var ok = OgcParameterValidator.TryParseBbox("1,2,3,4,EPSG:4326", out var bbox, out _);
        ok.Should().BeTrue();
        bbox.CrsToken.Should().Be("EPSG:4326");
        bbox.Is3D.Should().BeFalse();
    }

    [Fact]
    public void TryParseBbox_Accepts3dWithCrsSuffix()
    {
        var ok = OgcParameterValidator.TryParseBbox("1,2,10,3,4,20,EPSG:4979", out var bbox, out _);
        ok.Should().BeTrue();
        bbox.CrsToken.Should().Be("EPSG:4979");
        bbox.Is3D.Should().BeTrue();
    }

    [Fact]
    public void TryParseBbox_RejectsNull()
    {
        var ok = OgcParameterValidator.TryParseBbox(null, out _, out var error);
        ok.Should().BeFalse();
        error.Should().NotBeEmpty();
    }

    [Fact]
    public void TryParseBbox_RejectsEmpty()
    {
        var ok = OgcParameterValidator.TryParseBbox("", out _, out var error);
        ok.Should().BeFalse();
        error.Should().Contain("BBOX");
    }

    [Fact]
    public void TryParseBbox_RejectsWhitespace()
    {
        var ok = OgcParameterValidator.TryParseBbox("   ", out _, out var error);
        ok.Should().BeFalse();
        error.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("1")]
    [InlineData("1,2")]
    [InlineData("1,2,3")]
    [InlineData("1,2,3,4,5,6,7,8")]
    public void TryParseBbox_RejectsWrongTokenCount(string input)
    {
        var ok = OgcParameterValidator.TryParseBbox(input, out _, out var error);
        ok.Should().BeFalse();
        error.Should().Contain("4");
    }

    [Fact]
    public void TryParseBbox_RejectsNonNumericValue()
    {
        var ok = OgcParameterValidator.TryParseBbox("1,2,abc,4", out _, out var error);
        ok.Should().BeFalse();
        error.Should().Contain("numeric");
    }

    [Fact]
    public void TryParseBbox_RejectsNaN()
    {
        var ok = OgcParameterValidator.TryParseBbox("NaN,2,3,4", out _, out _);
        ok.Should().BeFalse();
    }

    [Fact]
    public void TryParseBbox_RejectsInfinity()
    {
        var ok = OgcParameterValidator.TryParseBbox("1,2,Infinity,4", out _, out _);
        ok.Should().BeFalse();
    }

    [Fact]
    public void TryParseBbox_RejectsReversedX()
    {
        var ok = OgcParameterValidator.TryParseBbox("10,2,3,4", out _, out var error);
        ok.Should().BeFalse();
        error.Should().Contain("ordered");
    }

    [Fact]
    public void TryParseBbox_RejectsReversedY()
    {
        var ok = OgcParameterValidator.TryParseBbox("1,10,3,4", out _, out var error);
        ok.Should().BeFalse();
        error.Should().Contain("ordered");
    }

    [Fact]
    public void TryParseBbox_RejectsReversedZ()
    {
        var ok = OgcParameterValidator.TryParseBbox("1,2,30,3,4,5", out _, out var error);
        ok.Should().BeFalse();
        error.Should().Contain("ordered");
    }

    [Fact]
    public void TryParseBbox_AllowsEqualBounds()
    {
        // Equal bounds are not a hard error in the kernel — protocol layers
        // may reject degenerate envelopes themselves.
        var ok = OgcParameterValidator.TryParseBbox("1,2,1,2", out var bbox, out _);
        ok.Should().BeTrue();
        bbox.MinX.Should().Be(bbox.MaxX);
    }

    [Fact]
    public void TryParseBbox_RejectsExcessiveLength()
    {
        var huge = new string('1', 300);
        var ok = OgcParameterValidator.TryParseBbox(huge + ",2,3,4", out _, out var error);
        ok.Should().BeFalse();
        error.Should().Contain("long");
    }

    [Fact]
    public void TryParseBbox_RejectsEmptyCrsSuffix()
    {
        var ok = OgcParameterValidator.TryParseBbox("1,2,3,4,", out _, out var error);
        ok.Should().BeFalse();
        error.Should().NotBeEmpty();
    }

    // ------------------------------------------------------------------
    // TryParseCrs
    // ------------------------------------------------------------------

    [Fact]
    public void TryParseCrs_AcceptsEpsgCode()
    {
        var ok = OgcParameterValidator.TryParseCrs("EPSG:4326", out var uri, out var error);
        ok.Should().BeTrue();
        error.Should().BeEmpty();
        uri.Should().Contain("4326");
    }

    [Fact]
    public void TryParseCrs_AcceptsBareSrid()
    {
        var ok = OgcParameterValidator.TryParseCrs("3857", out var uri, out _);
        ok.Should().BeTrue();
        uri.Should().Contain("3857");
    }

    [Fact]
    public void TryParseCrs_AcceptsOgcUri()
    {
        var ok = OgcParameterValidator.TryParseCrs("http://www.opengis.net/def/crs/EPSG/0/4326", out var uri, out _);
        ok.Should().BeTrue();
        uri.Should().Contain("4326");
    }

    [Fact]
    public void TryParseCrs_AcceptsOgcUrn()
    {
        var ok = OgcParameterValidator.TryParseCrs("urn:ogc:def:crs:EPSG::4326", out var uri, out _);
        ok.Should().BeTrue();
        uri.Should().Contain("4326");
    }

    [Fact]
    public void TryParseCrs_AcceptsCrs84()
    {
        var ok = OgcParameterValidator.TryParseCrs("CRS84", out var uri, out _);
        ok.Should().BeTrue();
        uri.Should().Contain("CRS84");
    }

    [Fact]
    public void TryParseCrs_RejectsNull()
    {
        var ok = OgcParameterValidator.TryParseCrs(null, out _, out var error);
        ok.Should().BeFalse();
        error.Should().Contain("CRS");
    }

    [Fact]
    public void TryParseCrs_RejectsEmpty()
    {
        var ok = OgcParameterValidator.TryParseCrs("", out _, out var error);
        ok.Should().BeFalse();
        error.Should().NotBeEmpty();
    }

    [Fact]
    public void TryParseCrs_RejectsGarbage()
    {
        var ok = OgcParameterValidator.TryParseCrs("not-a-crs", out _, out var error);
        ok.Should().BeFalse();
        error.Should().Contain("not-a-crs");
    }

    [Fact]
    public void TryParseCrs_RejectsZeroSrid()
    {
        var ok = OgcParameterValidator.TryParseCrs("EPSG:0", out _, out _);
        ok.Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // TryParseVersion
    // ------------------------------------------------------------------

    private static readonly IReadOnlyList<string> _wcsVersions = new[] { "2.0.1" };
    private static readonly IReadOnlyList<string> _wmsVersions = new[] { "1.3.0", "1.1.1" };

    [Fact]
    public void TryParseVersion_AcceptsSupportedVersion()
    {
        var ok = OgcParameterValidator.TryParseVersion("2.0.1", _wcsVersions, out var version, out var error);
        ok.Should().BeTrue();
        error.Should().BeEmpty();
        version.Should().Be("2.0.1");
    }

    [Fact]
    public void TryParseVersion_AcceptsLastSupportedVersion()
    {
        var ok = OgcParameterValidator.TryParseVersion("1.1.1", _wmsVersions, out var version, out _);
        ok.Should().BeTrue();
        version.Should().Be("1.1.1");
    }

    [Fact]
    public void TryParseVersion_IsCaseInsensitive()
    {
        var ok = OgcParameterValidator.TryParseVersion("2.0.1", _wcsVersions, out var version, out _);
        ok.Should().BeTrue();
        version.Should().Be("2.0.1");
    }

    [Fact]
    public void TryParseVersion_TrimsWhitespace()
    {
        var ok = OgcParameterValidator.TryParseVersion("  2.0.1  ", _wcsVersions, out var version, out _);
        ok.Should().BeTrue();
        version.Should().Be("2.0.1");
    }

    [Fact]
    public void TryParseVersion_RejectsUnknownVersion()
    {
        var ok = OgcParameterValidator.TryParseVersion("0.0.0", _wcsVersions, out _, out var error);
        ok.Should().BeFalse();
        error.Should().Contain("0.0.0");
        error.Should().Contain("2.0.1");
    }

    [Fact]
    public void TryParseVersion_RejectsNull()
    {
        var ok = OgcParameterValidator.TryParseVersion(null, _wcsVersions, out _, out var error);
        ok.Should().BeFalse();
        error.Should().Contain("VERSION");
    }

    [Fact]
    public void TryParseVersion_RejectsEmpty()
    {
        var ok = OgcParameterValidator.TryParseVersion("", _wcsVersions, out _, out _);
        ok.Should().BeFalse();
    }

    [Fact]
    public void TryParseVersion_ThrowsOnNullSupportedList()
    {
        var act = () => OgcParameterValidator.TryParseVersion("2.0.1", null!, out _, out _);
        act.Should().Throw<ArgumentNullException>();
    }

    // ------------------------------------------------------------------
    // TryParseLayers
    // ------------------------------------------------------------------

    [Fact]
    public void TryParseLayers_AcceptsSingleLayer()
    {
        var ok = OgcParameterValidator.TryParseLayers("foo", out var layers, out _);
        ok.Should().BeTrue();
        layers.Should().Equal("foo");
    }

    [Fact]
    public void TryParseLayers_AcceptsMultipleLayers()
    {
        var ok = OgcParameterValidator.TryParseLayers("foo,bar,baz", out var layers, out _);
        ok.Should().BeTrue();
        layers.Should().Equal("foo", "bar", "baz");
    }

    [Fact]
    public void TryParseLayers_TrimsTokens()
    {
        var ok = OgcParameterValidator.TryParseLayers(" foo , bar ", out var layers, out _);
        ok.Should().BeTrue();
        layers.Should().Equal("foo", "bar");
    }

    [Fact]
    public void TryParseLayers_DeduplicatesCaseInsensitively()
    {
        var ok = OgcParameterValidator.TryParseLayers("foo,FOO,bar", out var layers, out _);
        ok.Should().BeTrue();
        layers.Should().HaveCount(2);
        layers[0].Should().Be("foo");
        layers[1].Should().Be("bar");
    }

    [Fact]
    public void TryParseLayers_RejectsNull()
    {
        var ok = OgcParameterValidator.TryParseLayers(null, out _, out var error);
        ok.Should().BeFalse();
        error.Should().Contain("LAYERS");
    }

    [Fact]
    public void TryParseLayers_RejectsEmpty()
    {
        var ok = OgcParameterValidator.TryParseLayers("", out _, out _);
        ok.Should().BeFalse();
    }

    [Fact]
    public void TryParseLayers_RejectsEmptyToken()
    {
        var ok = OgcParameterValidator.TryParseLayers("foo,,bar", out _, out var error);
        ok.Should().BeFalse();
        error.Should().Contain("empty");
    }

    [Fact]
    public void TryParseLayers_RejectsExcessiveLength()
    {
        var huge = string.Join(",", Enumerable.Repeat("verylonglayer", 1000));
        var ok = OgcParameterValidator.TryParseLayers(huge, out _, out var error);
        ok.Should().BeFalse();
        error.Should().Contain("long");
    }

    // ------------------------------------------------------------------
    // TryParseTime
    // ------------------------------------------------------------------

    [Fact]
    public void TryParseTime_AcceptsInstant()
    {
        var ok = OgcParameterValidator.TryParseTime("2020-01-01T00:00:00Z", out var start, out var end, out _);
        ok.Should().BeTrue();
        start.Should().Be(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        end.Should().Be(start);
    }

    [Fact]
    public void TryParseTime_AcceptsClosedInterval()
    {
        var ok = OgcParameterValidator.TryParseTime(
            "2020-01-01T00:00:00Z/2020-12-31T23:59:59Z",
            out var start,
            out var end,
            out _);
        ok.Should().BeTrue();
        start.Year.Should().Be(2020);
        end!.Value.Year.Should().Be(2020);
        end.Value.Month.Should().Be(12);
    }

    [Fact]
    public void TryParseTime_AcceptsOpenEndInterval()
    {
        var ok = OgcParameterValidator.TryParseTime(
            "2020-01-01T00:00:00Z/..",
            out var start,
            out var end,
            out _);
        ok.Should().BeTrue();
        start.Year.Should().Be(2020);
        end.Should().BeNull();
    }

    [Fact]
    public void TryParseTime_AcceptsOpenStartInterval()
    {
        // Open-start collapses to an instant in the kernel: start = end, end = null
        var ok = OgcParameterValidator.TryParseTime(
            "../2020-12-31T00:00:00Z",
            out var start,
            out var end,
            out _);
        ok.Should().BeTrue();
        start.Year.Should().Be(2020);
        end.Should().BeNull();
    }

    [Fact]
    public void TryParseTime_RejectsReversedInterval()
    {
        var ok = OgcParameterValidator.TryParseTime(
            "2020-12-31T00:00:00Z/2020-01-01T00:00:00Z",
            out _,
            out _,
            out var error);
        ok.Should().BeFalse();
        error.Should().NotBeEmpty();
    }

    [Fact]
    public void TryParseTime_RejectsGarbage()
    {
        var ok = OgcParameterValidator.TryParseTime("not-a-date", out _, out _, out var error);
        ok.Should().BeFalse();
        error.Should().NotBeEmpty();
    }

    [Fact]
    public void TryParseTime_RejectsTripleSlash()
    {
        var ok = OgcParameterValidator.TryParseTime(
            "2020-01-01/2020-06-01/2020-12-01",
            out _,
            out _,
            out var error);
        ok.Should().BeFalse();
        error.Should().NotBeEmpty();
    }

    [Fact]
    public void TryParseTime_RejectsNull()
    {
        var ok = OgcParameterValidator.TryParseTime(null, out _, out _, out var error);
        ok.Should().BeFalse();
        error.Should().Contain("TIME");
    }

    [Fact]
    public void TryParseTime_RejectsEmpty()
    {
        var ok = OgcParameterValidator.TryParseTime("", out _, out _, out _);
        ok.Should().BeFalse();
    }

    [Fact]
    public void TryParseTime_RejectsOpenBothEnds()
    {
        var ok = OgcParameterValidator.TryParseTime("../..", out _, out _, out var error);
        ok.Should().BeFalse();
        error.Should().NotBeEmpty();
    }

    // ------------------------------------------------------------------
    // TryParseFormat
    // ------------------------------------------------------------------

    private static readonly IReadOnlyList<string> _formats = new[]
    {
        "image/tiff",
        "image/png",
        "image/jpeg",
    };

    [Fact]
    public void TryParseFormat_AcceptsExactMatch()
    {
        var ok = OgcParameterValidator.TryParseFormat("image/png", _formats, out var format, out _);
        ok.Should().BeTrue();
        format.Should().Be("image/png");
    }

    [Fact]
    public void TryParseFormat_IsCaseInsensitive()
    {
        var ok = OgcParameterValidator.TryParseFormat("IMAGE/PNG", _formats, out var format, out _);
        ok.Should().BeTrue();
        format.Should().Be("image/png");
    }

    [Fact]
    public void TryParseFormat_TrimsWhitespace()
    {
        var ok = OgcParameterValidator.TryParseFormat("  image/png  ", _formats, out var format, out _);
        ok.Should().BeTrue();
        format.Should().Be("image/png");
    }

    [Fact]
    public void TryParseFormat_RejectsUnknown()
    {
        var ok = OgcParameterValidator.TryParseFormat("application/netcdf", _formats, out _, out var error);
        ok.Should().BeFalse();
        error.Should().Contain("application/netcdf");
        error.Should().Contain("image/png");
    }

    [Fact]
    public void TryParseFormat_RejectsNull()
    {
        var ok = OgcParameterValidator.TryParseFormat(null, _formats, out _, out var error);
        ok.Should().BeFalse();
        error.Should().Contain("FORMAT");
    }

    [Fact]
    public void TryParseFormat_RejectsEmpty()
    {
        var ok = OgcParameterValidator.TryParseFormat("", _formats, out _, out _);
        ok.Should().BeFalse();
    }

    [Fact]
    public void TryParseFormat_RejectsWhitespace()
    {
        var ok = OgcParameterValidator.TryParseFormat("   ", _formats, out _, out _);
        ok.Should().BeFalse();
    }

    [Fact]
    public void TryParseFormat_ThrowsOnNullSupportedList()
    {
        var act = () => OgcParameterValidator.TryParseFormat("image/png", null!, out _, out _);
        act.Should().Throw<ArgumentNullException>();
    }
}
