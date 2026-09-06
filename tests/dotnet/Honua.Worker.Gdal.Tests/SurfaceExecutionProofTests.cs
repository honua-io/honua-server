// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Xunit;

namespace Honua.Worker.Gdal.Tests;

// Shares the pinned production runner and independent raster decoder with the
// raster proofs. The class-level RasterExecutionProof trait includes every case
// in the required PR Gate, without an optional-native-tool skip attribute.
public sealed partial class RasterExecutionProofTests
{
    [Theory]
    [InlineData("degrees", false)]
    [InlineData("percent", false)]
    [InlineData("degrees", true)]
    [InlineData("percent", true)]
    public async Task Slope_PlanarDem_MatchesRiseRunInBothUnitsAndNoData(string units, bool hole)
    {
        // Pixel spacing is 2m; elevation rises 2 east and 3 south per cell.
        // Catalog zFactor is a unit ratio (GDAL -s), so divide by 2.
        var gradient = Math.Sqrt(1 + 1.5 * 1.5) / 2;
        var expected = units == "percent" ? 100 * gradient : Math.Atan(gradient) * 180 / Math.PI;
        var output = await ExecuteRaster("surface.slope", ("source", SurfaceInput(hole ? "plane-hole.tif" : "plane.tif")),
            ("units", units), ("zFactor", "2"));
        AssertSurface(output, 5, 5, Neighborhood(5, 5, hole, (_, _) => expected));
    }

    [Theory]
    [InlineData("plane.tif", 2, 3, false)]
    [InlineData("plane-hole.tif", 2, 3, true)]
    [InlineData("east.tif", 2, 0, false)]
    [InlineData("north.tif", 0, -2, false)]
    [InlineData("flat.tif", 0, 0, false)]
    public async Task Aspect_PlanarAndFlatDem_MatchesDownslopeAzimuthAndNoData(string fixture, double east, double south, bool hole)
    {
        // Downslope east/north vector is (-east, south); bearing is clockwise
        // from north. Flat aspect is undefined, represented as nodata -9999.
        var bearing = east == 0 && south == 0 ? NoData : (Math.Atan2(-east, south) * 180 / Math.PI + 360) % 360;
        var output = await ExecuteRaster("surface.aspect", ("source", SurfaceInput(fixture)));
        AssertSurface(output, 5, 5, Neighborhood(5, 5, hole, (_, _) => bearing));
    }

    [Theory]
    [InlineData("rugosity-tpi", false)]
    [InlineData("rugosity-tpi", true)]
    [InlineData("rugosity-tri", false)]
    [InlineData("rugosity-tri", true)]
    [InlineData("roughness", false)]
    [InlineData("roughness", true)]
    public async Task Rugosity_PeakAndDepression_MatchesIndependentNeighborhoodStatistics(string operation, bool hole)
    {
        static double Elevation(int row, int col) => (row, col) switch { (2, 2) => 12, (1, 1) => -4, _ => 2 };
        var expected = Neighborhood(5, 5, hole, (row, col) =>
        {
            var center = Elevation(row, col);
            var neighbors = (from r in Enumerable.Range(row - 1, 3)
                             from c in Enumerable.Range(col - 1, 3)
                             where r != row || c != col
                             select Elevation(r, c)).ToArray();
            return operation switch
            {
                "rugosity-tpi" => center - neighbors.Average(),
                "rugosity-tri" => Math.Sqrt(neighbors.Sum(value => (value - center) * (value - center))),
                "roughness" => Math.Max(center, neighbors.Max()) - Math.Min(center, neighbors.Min()),
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };
        });
        var output = await ExecuteRaster("surface." + operation,
            ("source", SurfaceInput(hole ? "peak-depression-hole.tif" : "peak-depression.tif")), ("windowRadius", "1"));
        AssertSurface(output, 5, 5, expected);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(270)]
    public async Task Hillshade_Ridge_MatchesIlluminatedShadowedAndNoDataIntensities(int azimuth)
    {
        const double altitude = 20;
        // Fixed ridge rises 2 per 2m cell on the west, falls on the east.
        // Unit ratio 2 halves each gradient; vertical exaggeration remains 1.
        // Lambertian intensity: 1 + 254 * max(0, unit normal dot sun vector).
        var expected = Neighborhood(9, 5, false, (_, col) =>
        {
            var dx = col < 4 ? 0.5 : col > 4 ? -0.5 : 0;
            var normalDotSun = (Math.Sin(altitude * Math.PI / 180)
                - dx * Math.Sin(azimuth * Math.PI / 180) * Math.Cos(altitude * Math.PI / 180)) / Math.Sqrt(1 + dx * dx);
            return Math.Floor(1 + 254 * Math.Max(0, normalDotSun) + 0.5);
        }, nodata: 0);
        expected.Should().Contain(1, "the back slope must be shadowed, distinct from nodata zero");
        expected.Should().Contain(v => v > 100, "the opposing slope must be illuminated");
        var output = await ExecuteRaster("surface.hillshade", ("source", SurfaceInput("ridge-shade.tif")),
            ("azimuth", azimuth.ToString(CultureInfo.InvariantCulture)), ("altitude", "20"), ("zFactor", "2"));
        AssertSurface(output, 9, 5, expected, "Byte", 0, 0);
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(50, 0)]
    [InlineData(2, 50)]
    public async Task Viewshed_Ridge_MatchesVisibleHiddenAndOutOfRangeNoData(int observerHeight, int targetHeight)
    {
        // Observer at cell (3,1); a 10m ridge fills column 3. At this <=5m
        // range curvature is <2 micrometers, far below each visibility margin.
        // Low observer: cells through the crest are visible, those beyond are
        // hidden. Raising observer OR target to 50m clears the crest everywhere.
        var output = await ExecuteRaster("surface.viewshed", ("source", SurfaceInput("ridge-visibility.tif")),
            ("observerX", "1001.5"), ("observerY", "1996.5"),
            ("observerHeight", observerHeight.ToString(CultureInfo.InvariantCulture)),
            ("targetHeight", targetHeight.ToString(CultureInfo.InvariantCulture)), ("maxDistance", "5"));
        var expected = Enumerable.Range(0, 49).Select(i =>
        {
            var row = i / 7;
            var col = i % 7;
            return (col - 1) * (col - 1) + (row - 3) * (row - 3) > 25 ? 127d
                : col <= 3 || observerHeight == 50 || targetHeight == 50 ? 255d : 0d;
        }).ToArray();
        AssertGrid(output, 7, 7, 3857, [1000, 1, 0, 2000, 0, -1], 1);
        AssertBand(output, 0, expected, "Byte", 127);
    }

    [Fact]
    public async Task Contour_Ramp_MatchesLevelsCoordinatesAndLineTopology()
    {
        using var json = JsonDocument.Parse(await Execute("surface.contour", ("source", SurfaceInput("ramp.tif")),
            ("interval", "10"), ("base", "5")));
        var root = json.RootElement;
        root.GetProperty("type").GetString().Should().Be("FeatureCollection");
        root.GetProperty("crs").GetProperty("properties").GetProperty("name").GetString().Should().EndWith("3857");
        var features = root.GetProperty("features").EnumerateArray().OrderBy(f => f.GetProperty("properties").GetProperty("ELEV").GetDouble()).ToArray();
        features.Should().HaveCount(4);
        for (var i = 0; i < 4; i++)
        {
            var level = 5 + 10 * i;
            features[i].GetProperty("properties").GetProperty("ELEV").GetDouble().Should().Be(level);
            var geometry = new GeoJsonReader().Read<Geometry>(features[i].GetProperty("geometry").GetRawText());
            geometry.Should().BeOfType<LineString>();
            geometry.IsValid.Should().BeTrue();
            geometry.IsEmpty.Should().BeFalse();
            geometry.IsSimple.Should().BeTrue();
            // Elevation z=10*c at x=1001+2*c; linear interpolation gives
            // x=1001+level/5. GDAL extends end contours to the raster bounds.
            var x = 1001 + level / 5d;
            geometry.EnvelopeInternal.MinX.Should().BeApproximately(x, 1e-5);
            geometry.EnvelopeInternal.MaxX.Should().BeApproximately(x, 1e-5);
            geometry.EnvelopeInternal.MinY.Should().BeApproximately(1990, 1e-5);
            geometry.EnvelopeInternal.MaxY.Should().BeApproximately(2000, 1e-5);
            geometry.Length.Should().BeApproximately(10, 1e-5);
            var coordinates = geometry.Coordinates;
            coordinates.Should().HaveCountGreaterThan(2);
            var direction = Math.Sign(coordinates[^1].Y - coordinates[0].Y);
            for (var point = 0; point < coordinates.Length; point++)
            {
                coordinates[point].X.Should().BeApproximately(x, 1e-5);
                coordinates[point].Y.Should().BeInRange(1990, 2000);
                if (point > 0)
                {
                    (direction * (coordinates[point].Y - coordinates[point - 1].Y)).Should().BeGreaterThan(0);
                }
            }
        }
    }

    private static string SurfaceInput(string name) => Convert.ToBase64String(
        File.ReadAllBytes(Path.Join(AppContext.BaseDirectory, "Fixtures", "SurfaceProof", name)));

    private static double[] Neighborhood(int width, int height, bool cornerHole, Func<int, int, double> interior, double nodata = NoData)
        => Enumerable.Range(0, width * height).Select(i =>
        {
            var row = i / width;
            var col = i % width;
            return row == 0 || col == 0 || row == height - 1 || col == width - 1 || (cornerHole && row == 1 && col == 1)
                ? nodata : interior(row, col);
        }).ToArray();

    private static void AssertSurface(JsonElement raster, int width, int height, double[] expected,
        string type = "Float32", double nodata = NoData, double tolerance = 1e-5)
    {
        AssertGrid(raster, width, height, 3857, [1000, 2, 0, 2000, 0, -2], 1);
        AssertBand(raster, 0, expected, type, nodata, tolerance);
    }
}
