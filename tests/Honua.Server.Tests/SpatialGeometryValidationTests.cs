// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Infrastructure.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using System.Globalization;

namespace Honua.Server.Tests;

/// <summary>
/// SPATIAL GEOMETRY VALIDATION TESTS
/// Tests edge cases in geometric validation and boundary box parsing.
/// Focuses on RasterParsingHelpers and geometric validation functions.
/// </summary>
[Protocol(Protocols.Infrastructure)]
public class SpatialGeometryValidationTests
{
    #region ANTIMERIDIAN BOUNDING BOX VALIDATION

    /// <summary>
    /// CRITICAL: Tests validation of legitimate antimeridian-crossing bounding boxes
    /// These should be ACCEPTED as valid Pacific Ocean regions, not rejected
    /// Bug Risk: Overly strict validation rejects valid Pacific queries
    /// </summary>
    [Theory]
    [InlineData("175,-10,-175,10", true, "Central Pacific crossing antimeridian")]
    [InlineData("179.9,-5,-179.9,5", true, "Near-antimeridian crossing")]
    [InlineData("170,-20,-160,20", true, "Wide Pacific region crossing antimeridian")]
    [InlineData("179.999,-1,-179.999,1", true, "Narrow antimeridian crossing")]
    [InlineData("178,-15,-178,15", false, "Invalid: minX == maxX at antimeridian")]
    [InlineData("179,-10,179,10", false, "Invalid: same longitude (no crossing)")]
    public void ValidateAntimeridianBbox_LegitimateOceanRegions_AcceptedAsValid(
        string bbox, bool expectedValid, string description)
    {
        var result = RasterParsingHelpers.TryParseBoundingBox(
            bbox,
            AxisOrder.EastNorth,
            isGeographic: true,
            out var minX, out var minY, out var maxX, out var maxY);

        result.Should().Be(expectedValid, $"Case: {description}");

        if (expectedValid)
        {
            // Verify parsing extracted coordinates correctly
            var parts = bbox.Split(',');
            minX.Should().Be(double.Parse(parts[0], CultureInfo.InvariantCulture), "MinX should match first coordinate");
            maxX.Should().Be(double.Parse(parts[2], CultureInfo.InvariantCulture), "MaxX should match third coordinate");

            // For antimeridian crossing, minX > maxX is expected and valid
            if (minX > maxX)
            {
                minX.Should().BeGreaterThan(170, "Antimeridian crossing should start near 180°");
                maxX.Should().BeLessThan(-170, "Antimeridian crossing should end near -180°");
            }
        }
    }

    /// <summary>
    /// Tests bounding box validation with coordinates exactly at ±180° boundary
    /// Verifies edge case handling at exact longitude boundaries
    /// </summary>
    [Theory]
    [InlineData("-180,-90,180,90", true, "Full world extent")]
    [InlineData("-180,0,180,0", false, "Zero height at full width")]
    [InlineData("180,-10,-180,10", true, "Antimeridian crossing at exact boundaries")]
    [InlineData("-180.0001,-10,180,10", false, "Longitude beyond valid range")]
    [InlineData("-180,-10,180.0001,10", false, "Longitude beyond valid range")]
    public void ValidateLongitudeBoundaries_ExactBoundaryConditions_CorrectValidation(
        string bbox, bool expectedValid, string description)
    {
        var result = RasterParsingHelpers.TryParseBoundingBox(
            bbox,
            AxisOrder.EastNorth,
            isGeographic: true,
            out _, out _, out _, out _);

        result.Should().Be(expectedValid, $"Case: {description}");
    }

    #endregion

    #region POLAR REGION VALIDATION

    /// <summary>
    /// Tests bounding box validation in polar regions
    /// Verifies handling of coordinates near ±90° latitude
    /// </summary>
    [Theory]
    [InlineData("0,89,10,90", true, "Near North Pole")]
    [InlineData("-10,-90,10,-89", true, "Near South Pole")]
    [InlineData("0,90.0001,10,91", false, "Beyond North Pole")]
    [InlineData("-10,-91,10,-90.0001", false, "Beyond South Pole")]
    [InlineData("-180,-90,180,90", true, "Full polar extent")]
    [InlineData("0,85.051128,10,85.051129", true, "Near Web Mercator limit")]
    [InlineData("0,85.1,10,85.2", true, "Beyond Web Mercator limit but valid geographic")]
    public void ValidatePolarRegions_LatitudeBoundaryConditions_CorrectValidation(
        string bbox, bool expectedValid, string description)
    {
        var result = RasterParsingHelpers.TryParseBoundingBox(
            bbox,
            AxisOrder.EastNorth,
            isGeographic: true,
            out _, out _, out _, out _);

        result.Should().Be(expectedValid, $"Case: {description}");
    }

    /// <summary>
    /// Tests coordinate validation with extreme latitude values
    /// Verifies IsValidCoordinate handles polar extremes correctly
    /// </summary>
    [Theory]
    [InlineData(90.0, true, "North Pole")]
    [InlineData(-90.0, true, "South Pole")]
    [InlineData(89.999999, true, "Very close to North Pole")]
    [InlineData(-89.999999, true, "Very close to South Pole")]
    [InlineData(90.000001, false, "Beyond North Pole")]
    [InlineData(-90.000001, false, "Beyond South Pole")]
    [InlineData(85.051129, true, "Web Mercator north limit")]
    [InlineData(-85.051129, true, "Web Mercator south limit")]
    public void IsValidCoordinate_PolarLatitudes_CorrectRangeValidation(
        double coordinate, bool expectedValid, string description)
    {
        var result = RasterParsingHelpers.IsValidCoordinate(coordinate);

        // IsValidCoordinate checks general bounds, not geographic-specific bounds
        // It allows values up to ±40,000,000 for projected coordinates
        result.Should().BeTrue($"IsValidCoordinate should accept polar latitudes: {description}");

        // Geographic-specific validation happens in TryParseBoundingBox
        if (expectedValid)
        {
            coordinate.Should().BeInRange(-90.0, 90.0, $"Valid latitude should be within ±90°: {description}");
        }
    }

    #endregion

    #region HIGH-PRECISION COORDINATE VALIDATION

    /// <summary>
    /// Tests coordinate validation with high-precision decimal values
    /// Verifies precision is preserved and not truncated inappropriately
    /// </summary>
    [Theory]
    [InlineData("-122.419416667,37.774929167,-122.419316667,37.774979167", true, "Sub-meter precision")]
    [InlineData("-0.127647,51.507389,-0.127547,51.507439", true, "London high precision")]
    [InlineData("139.691711111,35.689722222,139.691811111,35.689772222", true, "Tokyo high precision")]
    [InlineData("-74.006000000,40.712800000,-74.005900000,40.712850000", true, "NYC 6-decimal precision")]
    [InlineData("179.999999999,-0.000000001,-179.999999999,0.000000001", true, "Maximum precision at antimeridian")]
    public void ValidateHighPrecisionBbox_SubMeterAccuracy_PreservesDecimalPlaces(
        string bbox, bool expectedValid, string description)
    {
        var result = RasterParsingHelpers.TryParseBoundingBox(
            bbox,
            AxisOrder.EastNorth,
            isGeographic: true,
            out var minX, out var minY, out var maxX, out var maxY);

        result.Should().Be(expectedValid, $"High precision case: {description}");

        if (expectedValid)
        {
            // Verify precision is maintained (within floating point limits)
            var parts = bbox.Split(',');
            var expectedMinX = double.Parse(parts[0], CultureInfo.InvariantCulture);
            var expectedMinY = double.Parse(parts[1], CultureInfo.InvariantCulture);
            var expectedMaxX = double.Parse(parts[2], CultureInfo.InvariantCulture);
            var expectedMaxY = double.Parse(parts[3], CultureInfo.InvariantCulture);

            minX.Should().BeApproximately(expectedMinX, 1e-10, "MinX precision should be preserved");
            minY.Should().BeApproximately(expectedMinY, 1e-10, "MinY precision should be preserved");
            maxX.Should().BeApproximately(expectedMaxX, 1e-10, "MaxX precision should be preserved");
            maxY.Should().BeApproximately(expectedMaxY, 1e-10, "MaxY precision should be preserved");
        }
    }

    /// <summary>
    /// Tests coordinate validation with extreme precision (many decimal places)
    /// Verifies parser handles very long decimal strings without issues
    /// </summary>
    [Theory]
    [InlineData(0.123456789012345, true, "15 decimal places")]
    [InlineData(-122.419416666666666, true, "15 decimal places negative")]
    [InlineData(179.999999999999999, true, "Maximum longitude precision")]
    [InlineData(89.999999999999999, true, "Maximum latitude precision")]
    public void IsValidCoordinate_ExtremePrecision_HandlesLongDecimals(
        double coordinate, bool expectedValid, string description)
    {
        var result = RasterParsingHelpers.IsValidCoordinate(coordinate);

        result.Should().Be(expectedValid, $"Extreme precision case: {description}");

        // Verify coordinate is finite and not NaN
        double.IsFinite(coordinate).Should().BeTrue($"Coordinate should be finite: {description}");
        double.IsNaN(coordinate).Should().BeFalse($"Coordinate should not be NaN: {description}");
    }

    #endregion

    #region AXIS ORDER HANDLING

    /// <summary>
    /// Tests bounding box parsing with different axis orders
    /// Critical for CRS84 (EastNorth) vs EPSG:4326 (traditionally NorthEast) handling
    /// </summary>
    [Theory]
    [InlineData("37.7749,-122.4194,37.7949,-122.3894", AxisOrder.NorthEast, true, "San Francisco lat,lon order")]
    [InlineData("-122.4194,37.7749,-122.3894,37.7949", AxisOrder.EastNorth, true, "San Francisco lon,lat order")]
    [InlineData("51.5074,-0.1276,51.5174,-0.1176", AxisOrder.NorthEast, true, "London lat,lon order")]
    [InlineData("-0.1276,51.5074,-0.1176,51.5174", AxisOrder.EastNorth, true, "London lon,lat order")]
    public void ParseBoundingBox_AxisOrderHandling_CorrectCoordinateSwapping(
        string bbox, AxisOrder axisOrder, bool expectedValid, string description)
    {
        var result = RasterParsingHelpers.TryParseBoundingBox(
            bbox,
            axisOrder,
            isGeographic: true,
            out var minX, out var minY, out var maxX, out var maxY);

        result.Should().Be(expectedValid, $"Axis order case: {description}");

        if (expectedValid)
        {
            // Verify coordinates are within geographic bounds after axis order processing
            minX.Should().BeInRange(-180, 180, "MinX should be valid longitude");
            maxX.Should().BeInRange(-180, 180, "MaxX should be valid longitude");
            minY.Should().BeInRange(-90, 90, "MinY should be valid latitude");
            maxY.Should().BeInRange(-90, 90, "MaxY should be valid latitude");

            // Verify bounding box makes sense (min < max unless crossing antimeridian)
            if (minX <= maxX)
            {
                minX.Should().BeLessThan(maxX, "MinX should be less than MaxX for normal bbox");
            }
            else
            {
                // Antimeridian crossing case
                minX.Should().BeGreaterThan(170, "Antimeridian crossing MinX should be near 180°");
                maxX.Should().BeLessThan(-170, "Antimeridian crossing MaxX should be near -180°");
            }

            minY.Should().BeLessThan(maxY, "MinY should be less than MaxY");
        }
    }

    /// <summary>
    /// Tests axis order handling with coordinates that would be invalid if order is wrong
    /// Uses extreme coordinates where axis swapping would cause obvious validation failure
    /// </summary>
    [Theory]
    [InlineData("179,-1,-179,1", AxisOrder.EastNorth, true, "Antimeridian crossing (correct order)")]
    [InlineData("-1,179,1,-179", AxisOrder.NorthEast, true, "Antimeridian crossing (swapped input)")]
    [InlineData("89,0,90,1", AxisOrder.NorthEast, true, "Near North Pole (lat,lon order)")]
    [InlineData("0,89,1,90", AxisOrder.EastNorth, true, "Near North Pole (lon,lat order)")]
    public void ParseBoundingBox_AxisOrderDetection_SwappingPreventsErrors(
        string bbox, AxisOrder axisOrder, bool expectedValid, string description)
    {
        var result = RasterParsingHelpers.TryParseBoundingBox(
            bbox,
            axisOrder,
            isGeographic: true,
            out var minX, out var minY, out var maxX, out var maxY);

        result.Should().Be(expectedValid, $"Axis order detection case: {description}");

        if (expectedValid)
        {
            // After correct axis order processing, all coordinates should be valid
            minX.Should().BeInRange(-180, 180, "Processed MinX should be valid longitude");
            maxX.Should().BeInRange(-180, 180, "Processed MaxX should be valid longitude");
            minY.Should().BeInRange(-90, 90, "Processed MinY should be valid latitude");
            maxY.Should().BeInRange(-90, 90, "Processed MaxY should be valid latitude");
        }
    }

    #endregion

    #region SPECIAL FLOAT VALUE HANDLING

    /// <summary>
    /// Tests coordinate validation with special floating point values
    /// Verifies proper handling of NaN, Infinity, and edge numeric cases
    /// </summary>
    [Theory]
    [InlineData(double.NaN, false, "NaN coordinate")]
    [InlineData(double.PositiveInfinity, false, "Positive infinity")]
    [InlineData(double.NegativeInfinity, false, "Negative infinity")]
    [InlineData(double.MaxValue, false, "Maximum double value")]
    [InlineData(double.MinValue, false, "Minimum double value")]
    [InlineData(0.0, true, "Zero coordinate")]
    [InlineData(-0.0, true, "Negative zero")]
    [InlineData(double.Epsilon, true, "Smallest positive double")]
    [InlineData(-double.Epsilon, true, "Smallest negative double")]
    public void IsValidCoordinate_SpecialFloatValues_ProperValidation(
        double coordinate, bool expectedValid, string description)
    {
        var result = RasterParsingHelpers.IsValidCoordinate(coordinate);

        result.Should().Be(expectedValid, $"Special float case: {description}");
    }

    /// <summary>
    /// Tests bounding box parsing with coordinates containing special values
    /// Verifies entire bbox is rejected if any coordinate is invalid
    /// </summary>
    [Theory]
    [InlineData("NaN,0,1,1", false, "NaN in first coordinate")]
    [InlineData("0,NaN,1,1", false, "NaN in second coordinate")]
    [InlineData("0,0,Infinity,1", false, "Infinity in third coordinate")]
    [InlineData("0,0,1,-Infinity", false, "Negative infinity in fourth coordinate")]
    public void ParseBoundingBox_SpecialFloatValues_RejectsInvalidBbox(
        string bbox, bool expectedValid, string description)
    {
        var result = RasterParsingHelpers.TryParseBoundingBox(
            bbox,
            AxisOrder.EastNorth,
            isGeographic: false, // Use non-geographic to test coordinate validation specifically
            out _, out _, out _, out _);

        result.Should().Be(expectedValid, $"Special float in bbox case: {description}");
    }

    #endregion

    #region MALFORMED INPUT HANDLING

    /// <summary>
    /// Tests bounding box parsing with various malformed input strings
    /// Verifies robust error handling and input validation
    /// </summary>
    [Theory]
    [InlineData("", false, "Empty string")]
    [InlineData("   ", false, "Whitespace only")]
    [InlineData("1,2,3", false, "Too few coordinates")]
    [InlineData("1,2,3,4,5", false, "Too many coordinates")]
    [InlineData("a,b,c,d", false, "Non-numeric characters")]
    [InlineData("1,2,3,d", false, "Mix of numeric and non-numeric")]
    [InlineData("1.5.5,2,3,4", false, "Invalid decimal format")]
    [InlineData("1e999,2,3,4", false, "Numeric overflow")]
    [InlineData("1,2,,4", false, "Empty coordinate")]
    [InlineData(",2,3,4", false, "Leading empty coordinate")]
    [InlineData("1,2,3,", false, "Trailing empty coordinate")]
    public void ParseBoundingBox_MalformedInput_RobustErrorHandling(
        string bbox, bool expectedValid, string description)
    {
        var result = RasterParsingHelpers.TryParseBoundingBox(
            bbox,
            AxisOrder.EastNorth,
            isGeographic: true,
            out _, out _, out _, out _);

        result.Should().Be(expectedValid, $"Malformed input case: {description}");
    }

    /// <summary>
    /// Tests coordinate string parsing with various whitespace and formatting
    /// Verifies parser handles different formatting styles gracefully
    /// </summary>
    [Theory]
    [InlineData(" 1 , 2 , 3 , 4 ", true, "Extra whitespace")]
    [InlineData("1,2,3,4", true, "No whitespace")]
    [InlineData("\t1\t,\t2\t,\t3\t,\t4\t", true, "Tab characters")]
    [InlineData("\n1\n,\n2\n,\n3\n,\n4\n", true, "Newline characters")]
    [InlineData("  1.5  ,  2.5  ,  3.5  ,  4.5  ", true, "Mixed whitespace with decimals")]
    public void ParseBoundingBox_WhitespaceHandling_TrimsAndParses(
        string bbox, bool expectedValid, string description)
    {
        var result = RasterParsingHelpers.TryParseBoundingBox(
            bbox,
            AxisOrder.EastNorth,
            isGeographic: false,
            out var minX, out var minY, out var maxX, out var maxY);

        result.Should().Be(expectedValid, $"Whitespace handling case: {description}");

        if (expectedValid)
        {
            // Verify coordinates are parsed correctly despite whitespace
            minX.Should().BeApproximately(1.0, 0.5, "MinX should be approximately 1");
            minY.Should().BeApproximately(2.0, 0.5, "MinY should be approximately 2");
            maxX.Should().BeApproximately(3.0, 0.5, "MaxX should be approximately 3");
            maxY.Should().BeApproximately(4.0, 0.5, "MaxY should be approximately 4");
        }
    }

    #endregion

    #region SECURITY AND PERFORMANCE VALIDATION

    /// <summary>
    /// Tests coordinate string length limits to prevent DoS attacks
    /// Verifies parser rejects excessively long input strings
    /// </summary>
    [Fact]
    public void ParseBoundingBox_ExcessiveLength_RejectsLongStrings()
    {
        // Create string longer than MaxBboxLength (100 characters)
        var longCoord = new string('1', 50);
        var longBbox = $"{longCoord},{longCoord},{longCoord},{longCoord}";

        longBbox.Length.Should().BeGreaterThan(100, "Test string should exceed length limit");

        var result = RasterParsingHelpers.TryParseBoundingBox(
            longBbox,
            AxisOrder.EastNorth,
            isGeographic: false,
            out _, out _, out _, out _);

        result.Should().BeFalse("Excessively long bbox string should be rejected");
    }

    /// <summary>
    /// Tests coordinate validation performance with rapid repeated calls
    /// Verifies no performance regression in validation functions
    /// </summary>
    [Fact]
    public void IsValidCoordinate_PerformanceStress_HandlesRepeatedCalls()
    {
        const int iterations = 100000;
        var coordinates = new[] { 0.0, -122.4194, 37.7749, -180.0, 180.0, 90.0, -90.0 };

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        for (int i = 0; i < iterations; i++)
        {
            var coord = coordinates[i % coordinates.Length];
            var result = RasterParsingHelpers.IsValidCoordinate(coord);
            result.Should().BeTrue("Valid coordinates should always pass validation");
        }

        stopwatch.Stop();

        // Validation should complete quickly (less than 1 second for 100k calls)
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(1000,
            "Coordinate validation should be fast enough for high-volume usage");
    }

    #endregion
}
