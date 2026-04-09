// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;

using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Infrastructure.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Infrastructure;

/// <summary>
/// Unit tests for <see cref="RasterParsingHelpers"/> shared parsing utilities.
/// These tests do NOT require Docker/database since they test pure static methods.
/// </summary>
[Protocol(Protocols.Infrastructure)]
public class RasterParsingHelpersTests
{
    #region TryParseBoundingBox

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParseBoundingBox_ValidGeographicBbox_ReturnsTrue()
    {
        var result = RasterParsingHelpers.TryParseBoundingBox(
            "-180,-90,180,90", out var minX, out var minY, out var maxX, out var maxY);

        result.Should().BeTrue();
        minX.Should().Be(-180);
        minY.Should().Be(-90);
        maxX.Should().Be(180);
        maxY.Should().Be(90);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParseBoundingBox_ValidProjectedBbox_ReturnsTrue()
    {
        // Web Mercator coordinates
        var result = RasterParsingHelpers.TryParseBoundingBox(
            "-20037508.34,-20037508.34,20037508.34,20037508.34",
            out var minX, out var minY, out var maxX, out var maxY);

        result.Should().BeTrue();
        minX.Should().BeApproximately(-20037508.34, 0.01);
        minY.Should().BeApproximately(-20037508.34, 0.01);
        maxX.Should().BeApproximately(20037508.34, 0.01);
        maxY.Should().BeApproximately(20037508.34, 0.01);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParseBoundingBox_ValidDecimalBbox_ReturnsTrue()
    {
        var result = RasterParsingHelpers.TryParseBoundingBox(
            "-122.4194,37.7749,-122.3894,37.7949",
            out var minX, out var minY, out var maxX, out var maxY);

        result.Should().BeTrue();
        minX.Should().BeApproximately(-122.4194, 0.0001);
        minY.Should().BeApproximately(37.7749, 0.0001);
        maxX.Should().BeApproximately(-122.3894, 0.0001);
        maxY.Should().BeApproximately(37.7949, 0.0001);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParseBoundingBox_WithWhitespace_TrimsAndReturnsTrue()
    {
        var result = RasterParsingHelpers.TryParseBoundingBox(
            " -180 , -90 , 180 , 90 ",
            out var minX, out var minY, out var maxX, out var maxY);

        result.Should().BeTrue();
        minX.Should().Be(-180);
        maxY.Should().Be(90);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParseBoundingBox_NorthEastGeographicAxisOrder_SwapsCoordinates()
    {
        var result = RasterParsingHelpers.TryParseBoundingBox(
            "37.7749,-122.4194,37.7949,-122.3894",
            AxisOrder.NorthEast,
            isGeographic: true,
            out var minX,
            out var minY,
            out var maxX,
            out var maxY);

        result.Should().BeTrue();
        minX.Should().BeApproximately(-122.4194, 0.0001);
        minY.Should().BeApproximately(37.7749, 0.0001);
        maxX.Should().BeApproximately(-122.3894, 0.0001);
        maxY.Should().BeApproximately(37.7949, 0.0001);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParseBoundingBox_NullInput_ReturnsFalse()
    {
        var result = RasterParsingHelpers.TryParseBoundingBox(
            null!, out _, out _, out _, out _);

        result.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParseBoundingBox_EmptyString_ReturnsFalse()
    {
        var result = RasterParsingHelpers.TryParseBoundingBox(
            "", out _, out _, out _, out _);

        result.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParseBoundingBox_WhitespaceOnly_ReturnsFalse()
    {
        var result = RasterParsingHelpers.TryParseBoundingBox(
            "   ", out _, out _, out _, out _);

        result.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParseBoundingBox_TooFewParts_ReturnsFalse()
    {
        var result = RasterParsingHelpers.TryParseBoundingBox(
            "-180,-90,180", out _, out _, out _, out _);

        result.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParseBoundingBox_TooManyParts_ReturnsFalse()
    {
        var result = RasterParsingHelpers.TryParseBoundingBox(
            "-180,-90,180,90,100", out _, out _, out _, out _);

        result.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParseBoundingBox_NonNumericValues_ReturnsFalse()
    {
        var result = RasterParsingHelpers.TryParseBoundingBox(
            "abc,def,ghi,jkl", out _, out _, out _, out _);

        result.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParseBoundingBox_PartiallyNonNumeric_ReturnsFalse()
    {
        var result = RasterParsingHelpers.TryParseBoundingBox(
            "-180,abc,180,90", out _, out _, out _, out _);

        result.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParseBoundingBox_InvertedXCoordinates_ReturnsFalse()
    {
        // maxX < minX
        var result = RasterParsingHelpers.TryParseBoundingBox(
            "180,-90,-180,90", out _, out _, out _, out _);

        result.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParseBoundingBox_InvertedYCoordinates_ReturnsFalse()
    {
        // maxY < minY
        var result = RasterParsingHelpers.TryParseBoundingBox(
            "-180,90,180,-90", out _, out _, out _, out _);

        result.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParseBoundingBox_EqualXCoordinates_ReturnsFalse()
    {
        // minX == maxX (zero-width bbox)
        var result = RasterParsingHelpers.TryParseBoundingBox(
            "10,-90,10,90", out _, out _, out _, out _);

        result.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParseBoundingBox_EqualYCoordinates_ReturnsFalse()
    {
        // minY == maxY (zero-height bbox)
        var result = RasterParsingHelpers.TryParseBoundingBox(
            "-180,45,180,45", out _, out _, out _, out _);

        result.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParseBoundingBox_CoordinatesExceedMaxBound_ReturnsFalse()
    {
        // Values beyond ±40,000,000
        var result = RasterParsingHelpers.TryParseBoundingBox(
            "-50000000,-50000000,50000000,50000000", out _, out _, out _, out _);

        result.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParseBoundingBox_CoordinatesAtMaxBound_ReturnsTrue()
    {
        var result = RasterParsingHelpers.TryParseBoundingBox(
            "-40000000,-40000000,40000000,40000000", out _, out _, out _, out _);

        result.Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParseBoundingBox_ExceedsMaxLength_ReturnsFalse()
    {
        // Create a bbox string longer than 100 characters
        var longBbox = string.Join(",", new string('1', 30), new string('2', 30), new string('3', 30), new string('4', 30));

        var result = RasterParsingHelpers.TryParseBoundingBox(
            longBbox, out _, out _, out _, out _);

        result.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParseBoundingBox_SpecialFloatValues_ReturnsFalse()
    {
        // NaN
        var result = RasterParsingHelpers.TryParseBoundingBox(
            "NaN,-90,180,90", out _, out _, out _, out _);

        result.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParseBoundingBox_GeographicCoordinatesOutOfRange_ReturnsFalse()
    {
        var result = RasterParsingHelpers.TryParseBoundingBox(
            "-200,-95,180,90",
            AxisOrder.EastNorth,
            isGeographic: true,
            out _,
            out _,
            out _,
            out _);

        result.Should().BeFalse();
    }

    #endregion

    #region IsValidCoordinate

    [UnitTest]
    [Operation(Operations.Query)]
    public void IsValidCoordinate_ZeroValue_ReturnsTrue()
    {
        RasterParsingHelpers.IsValidCoordinate(0).Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void IsValidCoordinate_NormalGeographicValue_ReturnsTrue()
    {
        RasterParsingHelpers.IsValidCoordinate(45.5).Should().BeTrue();
        RasterParsingHelpers.IsValidCoordinate(-122.4).Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void IsValidCoordinate_WebMercatorValue_ReturnsTrue()
    {
        RasterParsingHelpers.IsValidCoordinate(20037508.34).Should().BeTrue();
        RasterParsingHelpers.IsValidCoordinate(-20037508.34).Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void IsValidCoordinate_AtMaxBound_ReturnsTrue()
    {
        RasterParsingHelpers.IsValidCoordinate(40000000).Should().BeTrue();
        RasterParsingHelpers.IsValidCoordinate(-40000000).Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void IsValidCoordinate_BeyondMaxBound_ReturnsFalse()
    {
        RasterParsingHelpers.IsValidCoordinate(40000001).Should().BeFalse();
        RasterParsingHelpers.IsValidCoordinate(-40000001).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void IsValidCoordinate_NaN_ReturnsFalse()
    {
        RasterParsingHelpers.IsValidCoordinate(double.NaN).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void IsValidCoordinate_PositiveInfinity_ReturnsFalse()
    {
        RasterParsingHelpers.IsValidCoordinate(double.PositiveInfinity).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void IsValidCoordinate_NegativeInfinity_ReturnsFalse()
    {
        RasterParsingHelpers.IsValidCoordinate(double.NegativeInfinity).Should().BeFalse();
    }

    #endregion

    #region ParseRasterFormat

    [UnitTest]
    [Operation(Operations.Query)]
    public void ParseRasterFormat_Png_ReturnsPNG()
    {
        RasterParsingHelpers.ParseRasterFormat("png").Should().Be(RasterFormat.PNG);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void ParseRasterFormat_PngUpperCase_ReturnsPNG()
    {
        RasterParsingHelpers.ParseRasterFormat("PNG").Should().Be(RasterFormat.PNG);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void ParseRasterFormat_Jpg_ReturnsJPEG()
    {
        RasterParsingHelpers.ParseRasterFormat("jpg").Should().Be(RasterFormat.JPEG);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void ParseRasterFormat_Jpeg_ReturnsJPEG()
    {
        RasterParsingHelpers.ParseRasterFormat("jpeg").Should().Be(RasterFormat.JPEG);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void ParseRasterFormat_Tiff_ReturnsTIFF()
    {
        RasterParsingHelpers.ParseRasterFormat("tiff").Should().Be(RasterFormat.TIFF);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void ParseRasterFormat_Tif_ReturnsTIFF()
    {
        RasterParsingHelpers.ParseRasterFormat("tif").Should().Be(RasterFormat.TIFF);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void ParseRasterFormat_UnknownFormat_DefaultsToPNG()
    {
        RasterParsingHelpers.ParseRasterFormat("bmp").Should().Be(RasterFormat.PNG);
        RasterParsingHelpers.ParseRasterFormat("webp").Should().Be(RasterFormat.PNG);
        RasterParsingHelpers.ParseRasterFormat("gif").Should().Be(RasterFormat.PNG);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void ParseRasterFormat_EmptyString_DefaultsToPNG()
    {
        RasterParsingHelpers.ParseRasterFormat("").Should().Be(RasterFormat.PNG);
    }

    #endregion

    #region TryParseSrid

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParseSrid_NullInput_ReturnsNull()
    {
        var result = SpatialReferenceHelpers.TryParseSrid(null);
        result.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParseSrid_EmptyString_ReturnsNull()
    {
        var result = SpatialReferenceHelpers.TryParseSrid("");
        result.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParseSrid_BareSridNumber_ReturnsSrid()
    {
        var result = SpatialReferenceHelpers.TryParseSrid("4326");
        result.Should().Be(4326);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParseSrid_EpsgPrefix_ReturnsSrid()
    {
        var result = SpatialReferenceHelpers.TryParseSrid("EPSG:3857");
        result.Should().Be(3857);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParseSrid_EpsgPrefixCaseInsensitive_ReturnsSrid()
    {
        var result = SpatialReferenceHelpers.TryParseSrid("epsg:4326");
        result.Should().Be(4326);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParseSrid_OgcUri_ReturnsSrid()
    {
        var result = SpatialReferenceHelpers.TryParseSrid("http://www.opengis.net/def/crs/EPSG/0/4326");
        result.Should().Be(4326);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParseSrid_SafeCurieEpsg_ReturnsSrid()
    {
        var result = SpatialReferenceHelpers.TryParseSrid("[EPSG:3857]");
        result.Should().Be(3857);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParseSrid_SafeCurieCrs84_Returns4326()
    {
        var result = SpatialReferenceHelpers.TryParseSrid("[OGC:CRS84]");
        result.Should().Be(4326);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParseSrid_UrnEpsg_ReturnsSrid()
    {
        var result = SpatialReferenceHelpers.TryParseSrid("urn:ogc:def:crs:EPSG::32633");
        result.Should().Be(32633);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParseSrid_Crs84Uri_Returns4326()
    {
        var result = SpatialReferenceHelpers.TryParseSrid("http://www.opengis.net/def/crs/OGC/1.3/CRS84");
        result.Should().Be(4326);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParseSrid_InvalidString_ReturnsNull()
    {
        var result = SpatialReferenceHelpers.TryParseSrid("not-a-crs");
        result.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParseSrid_ZeroSrid_ReturnsNull()
    {
        var result = SpatialReferenceHelpers.TryParseSrid("0");
        result.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParseSrid_NegativeSrid_ReturnsNull()
    {
        var result = SpatialReferenceHelpers.TryParseSrid("-1");
        result.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParseSrid_LowercaseCrs84_Returns4326()
    {
        var result = SpatialReferenceHelpers.TryParseSrid("http://www.opengis.net/def/crs/OGC/1.3/crs84");
        result.Should().Be(4326);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParseSrid_MixedCaseCrs84_Returns4326()
    {
        var result = SpatialReferenceHelpers.TryParseSrid("http://www.opengis.net/def/crs/OGC/1.3/Crs84");
        result.Should().Be(4326);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParseSrid_BareCrs84_Returns4326()
    {
        var result = SpatialReferenceHelpers.TryParseSrid("CRS84");
        result.Should().Be(4326);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParseSrid_EpsgPrefixNoNumber_ReturnsNull()
    {
        var result = SpatialReferenceHelpers.TryParseSrid("EPSG:");
        result.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParseSrid_PrefixedJunkBeforeOgcUri_ReturnsNull()
    {
        var result = SpatialReferenceHelpers.TryParseSrid("prefixhttp://www.opengis.net/def/crs/EPSG/0/4326");
        result.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParseSrid_SuffixedCrs84Text_ReturnsNull()
    {
        var result = SpatialReferenceHelpers.TryParseSrid("prefix-CRS84");
        result.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParseSrid_OgcUriWithTrailingPath_ReturnsNull()
    {
        var result = SpatialReferenceHelpers.TryParseSrid("http://www.opengis.net/def/crs/EPSG/0/4326/extra");
        result.Should().BeNull();
    }

    #endregion

    #region ParseRasterFormat Additional Cases

    [UnitTest]
    [Operation(Operations.Query)]
    public void ParseRasterFormat_NullInput_ReturnsPng()
    {
        var result = RasterParsingHelpers.ParseRasterFormat(null);
        result.Should().Be(RasterFormat.PNG);
    }

    #endregion
}
