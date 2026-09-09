// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Worker.Gdal.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.IO;
using Xunit;
using Xunit.Sdk;

namespace Honua.Worker.Gdal.Tests;

/// <summary>Real production executors and pinned native GDAL, with analytical cell oracles.</summary>
[Trait("Category", "RasterExecutionProof")]
public sealed partial class RasterExecutionProofTests : IDisposable
{
    private const double NoData = -9999;
    private static readonly double[] Grid = [1, 2, 3, 4, 5, NoData, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
    private readonly string _scratch = Path.Join(AppContext.BaseDirectory, "raster-proof", Guid.NewGuid().ToString("N"));
    private readonly DockerGdalCommandRunner _runner = GdalProofRuntime.CreateRunner();

    [Fact]
    public async Task Clip_LShapedCutline_PreservesInsideBoundaryAndNoDataCells()
    {
        using var cutline = JsonDocument.Parse(await File.ReadAllTextAsync(Fixture("cutline.geojson")));
        var geometry = new GeoJsonReader().Read<NetTopologySuite.Geometries.Geometry>(
            cutline.RootElement.GetProperty("features")[0].GetProperty("geometry").GetRawText());
        var output = await ExecuteRaster("raster.clip", ("source", Input("grid.tif")),
            ("boundary", Convert.ToBase64String(geometry.AsBinary())), ("boundarySrid", "4326"));
        AssertGrid(output, 3, 3, 4326, [1, 1, 0, 4, 0, -1], 2);
        // The L selects column 1 and row 2. Cell (1,1) is source nodata;
        // cells to the east of its upright are outside even within the crop extent.
        AssertBand(output, 0, [2, NoData, NoData, NoData, NoData, NoData, 10, 11, 12]);
        AssertBand(output, 1, [20, NoData, NoData, NoData, NoData, NoData, 100, 110, 120]);
    }

    [Fact]
    public async Task Clip_SourceWithoutNoData_UsesInternalMaskAndPreservesValidZero()
    {
        using var cutline = JsonDocument.Parse(await File.ReadAllTextAsync(Fixture("cutline.geojson")));
        var geometry = new GeoJsonReader().Read<NetTopologySuite.Geometries.Geometry>(
            cutline.RootElement.GetProperty("features")[0].GetProperty("geometry").GetRawText());
        var output = await ExecuteRaster("raster.clip", ("source", Input("grid-unmasked.tif")),
            ("boundary", Convert.ToBase64String(geometry.AsBinary())), ("boundarySrid", "4326"));
        AssertGrid(output, 3, 3, 4326, [1, 1, 0, 4, 0, -1], 2);
        int[] mask = [255, 0, 0, 255, 0, 0, 255, 255, 255];
        AssertBand(output, 0, [0, 0, 0, 6, 0, 0, 10, 11, 12], nodata: null, mask: mask);
        AssertBand(output, 1, [0, 0, 0, 60, 0, 0, 100, 110, 120], nodata: null, mask: mask);
    }

    [Fact]
    public async Task ZonalStatistics_SourceWithoutNoData_ExcludesCutlineExteriorButCountsValidZero()
    {
        using var json = JsonDocument.Parse(await Execute("raster.zonal-statistics", ("source", Input("grid-unmasked.tif")),
            ("zones", Input("cutline.geojson"))));
        var zone = json.RootElement.GetProperty("zones").EnumerateArray().Should().ContainSingle().Which;
        AssertZone(zone, "L", 5, 0, 12, 39);
    }

    [Fact]
    public async Task ZonalStatistics_DisjointOverlappingAndEmptyZones_MatchHandDerivedAggregates()
    {
        using var json = JsonDocument.Parse(await Execute("raster.zonal-statistics", ("source", Input("grid.tif")),
            ("zones", Input("zones.geojson")), ("band", "2")));
        var root = json.RootElement;
        root.GetProperty("band").GetInt32().Should().Be(2);
        var zones = root.GetProperty("zones").EnumerateArray().ToArray();
        zones.Should().HaveCount(4);
        // Band two: left {10,20,50}, right {30,40,70,80}, overlap {20,30,70}.
        AssertZone(zones[0], "left", 3, 10, 50, 80);
        AssertZone(zones[1], "right", 4, 30, 80, 220);
        AssertZone(zones[2], "overlap", 3, 20, 70, 120);
        zones[3].GetProperty("zoneId").GetString().Should().Be("nodata");
        zones[3].TryGetProperty("skipped", out _).Should().BeFalse();
        zones[3].GetProperty("count").GetDouble().Should().Be(0);
        zones[3].GetProperty("sum").GetDouble().Should().Be(0);
        foreach (var name in new[] { "mean", "min", "max", "stddev" })
        {
            zones[3].GetProperty(name).ValueKind.Should().Be(JsonValueKind.Null);
        }
    }

    [Fact]
    public async Task ZonalStatistics_MultipleReadWindows_MergesExactCountsSumsAndPopulationVariance()
    {
        using var json = JsonDocument.Parse(await Execute("raster.zonal-statistics", ("source", Input("zonal-wide.tif")),
            ("zones", Input("zones-wide.geojson")), ("statistics", "count,sum,mean,min,max,stddev,variance")));
        var zone = json.RootElement.GetProperty("zones").EnumerateArray().Should().ContainSingle().Which;
        // Two rows 0..512 excluding 256. Each row crosses two 256-cell
        // windows and the final one-cell window; derive moments algebraically.
        const int count = 1024;
        const double sum = 2 * (512 * 513 / 2 - 256);
        const double squareSum = 2 * (512.0 * 513 * 1025 / 6 - 256 * 256);
        var variance = squareSum / count - Math.Pow(sum / count, 2);
        AssertZone(zone, "wide", count, 0, 512, sum);
        zone.GetProperty("variance").GetDouble().Should().BeApproximately(variance, 1e-9);
        zone.GetProperty("stddev").GetDouble().Should().BeApproximately(Math.Sqrt(variance), 1e-9);
    }

    [Theory]
    [InlineData("ndvi", true, false)]
    [InlineData("evi", true, false)]
    [InlineData("ndvi", true, true)]
    [InlineData("ndvi", false, false)]
    public async Task SpectralIndex_ReflectanceBands_MatchFormulaIncludingUndefinedAndNoData(string index, bool sourceHasNoData, bool overrideNoData)
    {
        Directory.CreateDirectory(_scratch);
        File.Copy(Fixture(sourceHasNoData ? "reflectance.tif" : "reflectance-unmasked.tif"), Path.Join(_scratch, "reflectance.tif"));
        var inputs = new List<(string, string)> { ("index", index) };
        var outputNoData = overrideNoData ? -1234 : sourceHasNoData ? NoData : double.NaN;
        if (overrideNoData)
        {
            inputs.Add(("noData", "-1234"));
        }
        foreach (var (role, band) in new[] { ("red", "1"), ("nir", "2"), ("blue", "3") })
        {
            await Run("gdal_translate", ["-b", band, "reflectance.tif", role + ".tif"]);
            inputs.Add((role, Convert.ToBase64String(await File.ReadAllBytesAsync(Path.Join(_scratch, role + ".tif")))));
        }
        var output = await ExecuteRaster("raster.spectral-index", inputs.ToArray());
        AssertGrid(output, 4, 2, 4326, [0, 1, 0, 4, 0, -1], 1);
        double[] red = [0.2, 0.1, 0, NoData, 0.3, 0.2, 0.4, 0.1];
        double[] nir = [0.6, 0.5, 0, 0.8, 0.3, 0.8, 0.2, NoData];
        double[] blue = [0.1, 0.05, 2.0 / 15, 0.1, 0.2, 0.1, 0.1, 0.1];
        var expected = Enumerable.Range(0, 8).Select(i => sourceHasNoData && (red[i] == NoData || nir[i] == NoData) ? outputNoData
            : index == "ndvi" ? (nir[i] - red[i]) / (nir[i] + red[i])
            : 2.5 * (nir[i] - red[i]) / (nir[i] + 6 * red[i] - 7.5 * blue[i] + 1)).ToArray();
        AssertBand(output, 0, expected.Select(v => double.IsFinite(v) ? v : outputNoData).ToArray(), nodata: outputNoData, tolerance: 1e-6);
    }

    [Theory]
    [InlineData("raster.reproject", "nearest")]
    [InlineData("raster.reproject", "bilinear")]
    [InlineData("gdal.gdalwarp", "nearest")]
    [InlineData("conversion.raster-reproject", "nearest")]
    [InlineData("conversion.raster-reproject", "bilinear")]
    public async Task Reproject_GeographicToMercator_MatchesAnalyticalGridAndInverseMappedSamples(string processId, string resampling)
    {
        var source = await Decode(await File.ReadAllBytesAsync(Fixture("grid.tif")));
        AssertGrid(source, 4, 4, 4326, [0, 1, 0, 4, 0, -1], 2);
        AssertBand(source, 0, Grid);
        var output = await ExecuteRaster(processId, ("source", Input("grid.tif")),
            (processId == "gdal.gdalwarp" ? "targetSrs" : "targetSrid", "3857"),
            (processId == "gdal.gdalwarp" ? "sourceSrs" : "resampling", processId == "gdal.gdalwarp" ? "EPSG:4326" : resampling));
        // Spherical Mercator EPSG:3857 uses R=6378137. GDAL's suggested square
        // pixel spans the transformed diagonal divided by the source diagonal.
        const double radius = 6378137;
        var east = radius * 4 * Math.PI / 180;
        var north = radius * Math.Log(Math.Tan(Math.PI / 4 + 2 * Math.PI / 180));
        var cell = Math.Sqrt((east * east + north * north) / 32);
        AssertGrid(output, 4, 4, 3857, [0, cell, 0, north, 0, -cell], 2, 0.001);
        var expected = new double[16];
        for (var row = 0; row < 4; row++)
        {
            for (var col = 0; col < 4; col++)
            {
                var longitude = (col + 0.5) * cell / radius * 180 / Math.PI;
                var latitude = (2 * Math.Atan(Math.Exp((north - (row + 0.5) * cell) / radius)) - Math.PI / 2) * 180 / Math.PI;
                expected[row * 4 + col] = Sample(longitude - 0.5, 3.5 - latitude, resampling);
            }
        }
        AssertBand(output, 0, expected, tolerance: 0.003);
        AssertBand(output, 1, expected.Select(v => v == NoData ? v : v * 10).ToArray(), tolerance: 0.03);
    }

    [Fact]
    public async Task WarpOracle_ChangedPixelInValidGeoTiff_IsRejected()
    {
        var bytes = await Execute("gdal.gdalwarp", ("source", Input("grid.tif")), ("targetSrs", "EPSG:3857"));
        Directory.CreateDirectory(_scratch);
        await File.WriteAllBytesAsync(Path.Join(_scratch, "wrong.tif"), bytes);
        await Run("python3", ["-c", "from osgeo import gdal; d=gdal.Open('wrong.tif',gdal.GA_Update); b=d.GetRasterBand(1); a=b.ReadAsArray(); a[0,0]=42; b.WriteArray(a); d=None"]);
        var wrong = await Decode(await File.ReadAllBytesAsync(Path.Join(_scratch, "wrong.tif")));
        // Grid, CRS, nodata, and TIFF structure remain valid; only a cell is wrong.
        Action assert = () => AssertBand(wrong, 0, Grid);
        assert.Should().Throw<XunitException>().Which.Message.Should().Contain("cell 0");
    }

    [Theory]
    [InlineData("first")]
    [InlineData("last")]
    public async Task Mosaic_OverlappingTwoBandTiles_ProvesPrecedenceAndNoDataFallback(string policy)
    {
        var output = await ExecuteRaster("raster.mosaic", ("sources", Input("mosaic-a.tif") + "|" + Input("mosaic-b.tif")),
            ("operator", policy), ("resampling", "nearest"));
        AssertGrid(output, 5, 2, 4326, [0, 1, 0, 2, 0, -1], 2);
        AssertBand(output, 0, [1, 2, policy == "first" ? 3 : 30, 40, 50, 4, 5, 60, NoData, 80]);
        AssertBand(output, 1, [11, 12, policy == "first" ? 13 : 130, 140, 150, 14, 15, 160, NoData, 180]);
    }

    [Theory]
    [InlineData("nearest")]
    [InlineData("bilinear")]
    public async Task Resample_HalfSizeCells_MatchesNearestOrBilinearWeightsAndNoData(string resampling)
    {
        var output = await ExecuteRaster("raster.resample", ("source", Input("grid.tif")),
            ("cellSize", "0.5"), ("resampling", resampling));
        AssertGrid(output, 8, 8, 4326, [0, 0.5, 0, 4, 0, -0.5], 2);
        var expected = Enumerable.Range(0, 64).Select(i => Sample((i % 8 + 0.5) / 2 - 0.5,
            (i / 8 + 0.5) / 2 - 0.5, resampling)).ToArray();
        AssertBand(output, 0, expected, tolerance: 1e-5);
        AssertBand(output, 1, expected.Select(v => v == NoData ? v : v * 10).ToArray(), tolerance: 1e-4);
    }

    [Theory]
    [InlineData(false, 100)]
    [InlineData(true, 100)]
    [InlineData(true, 0)]
    public async Task InterpolateIdw_KnownPoints_MatchesInverseDistanceAndEmptySearchCells(bool bounded, double centerValue)
    {
        var inputs = new List<(string, string)> { ("points", Input(centerValue == 0 ? "points-zero.geojson" : "points.geojson")), ("zField", "value"), ("width", "5"), ("height", "5") };
        if (bounded)
        {
            inputs.Add(("radius", "0.1"));
        }
        var output = await ExecuteRaster("raster.interpolate-idw", inputs.ToArray());
        AssertGrid(output, 5, 5, 4326, [0, 0.8, 0, 4, 0, -0.8], 1);
        (double X, double Y, double Value)[] points = [(0, 0, 10), (4, 0, 20), (0, 4, 30), (4, 4, 40), (2, 2, centerValue)];
        var expected = new double[25];
        for (var i = 0; i < 25; i++)
        {
            var x = (i % 5 + 0.5) * 0.8;
            var y = 4 - (i / 5 + 0.5) * 0.8;
            if (i == 12)
            {
                expected[i] = centerValue; // Exact source values, including valid zero, take precedence.
                continue;
            }
            var weights = points.Select(p => (p.Value, Distance: (p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y)))
                .Where(p => !bounded || p.Distance <= 0.01).ToArray();
            expected[i] = weights.Length == 0 ? double.NaN : weights.Sum(p => p.Value / p.Distance) / weights.Sum(p => 1 / p.Distance);
        }
        // Native invdist's default SSE/AVX path uses reduced precision even
        // though the output is Float64 (GDALGridCreate documentation). Budget
        // eight float rounding units at the fixture's maximum magnitude, not
        // a tolerance fitted to the observed output. Coincident values stay exact.
        var tolerance = bounded ? 1e-9 : 8 * Math.ScaleB(1, -23) * 100;
        AssertBand(output, 0, expected, "Float64", double.NaN, tolerance);
        output.GetProperty("bands")[0].GetProperty("values")[12].GetDouble().Should().Be(centerValue);
    }

    /// <summary>
    /// Ordinary kriging is an EXACT interpolator: at a sample location the estimator
    /// reproduces that sample's observed value, which is the property that separates it
    /// from a smoother. The fixture's centre sample sits on a cell centre for an odd grid
    /// size, so the oracle needs no reference implementation — the decoded cell must equal
    /// the observation. The grid metadata is asserted against the same geotransform the
    /// IDW proof pins, so both interpolators demonstrably land on one grid.
    /// </summary>
    [Fact]
    public async Task InterpolateKriging_SamplePoints_ReproducesSampleValuesExactly()
    {
        var output = await ExecuteRaster(
            "raster.interpolate-kriging",
            ("points", Input("points.geojson")), ("zField", "value"), ("width", "5"), ("height", "5"));

        AssertGrid(output, 5, 5, 4326, [0, 0.8, 0, 4, 0, -0.8], 1);
        var band = output.GetProperty("bands")[0];
        band.GetProperty("type").GetString().Should().Be("Float32");
        // Every cell is predicted from the global sample set, so the surface declares no
        // nodata and is fully valid — there is no hole for a caller to misread as data.
        band.GetProperty("nodata").ValueKind.Should().Be(JsonValueKind.Null);
        band.GetProperty("mask").EnumerateArray().Select(v => v.GetInt32()).Should().OnlyContain(v => v == 255);

        // Cell (2,2) of a 5×5 grid over [0,4]² is centred on (2,2) — the centre sample.
        var values = band.GetProperty("values").EnumerateArray().ToArray();
        values.Should().HaveCount(25);
        values[12].GetDouble().Should().BeApproximately(100, 1e-4, "kriging is exact at a sample location");
    }

    /// <summary>
    /// Two samples give the ordinary-kriging system a closed-form solution:
    /// <c>w₂ - w₁ = (γ₀₁ - γ₀₂) / γ₁₂</c> under <c>w₁ + w₂ = 1</c>. The expectations below
    /// are computed from that closed form and the spherical semivariogram, independently of
    /// the executor's general dual solve, and the pinned variogram removes every fitted
    /// default from the oracle.
    /// </summary>
    [Fact]
    public async Task InterpolateKriging_TwoSymmetricSamples_MatchesHandDerivedMidpointAndGridMetadata()
    {
        var output = await ExecuteRaster(
            "raster.interpolate-kriging",
            ("points", Input("points-pair.geojson")), ("zField", "value"),
            ("model", "spherical"), ("range", "4"), ("sill", "1"), ("nugget", "0"),
            ("width", "3"), ("height", "1"));

        // The samples are collinear, so the Y extent is degenerate and is widened by a
        // unit box: rows span [-0.5, 0.5] with the origin at the north-west corner.
        AssertGrid(output, 3, 1, 4326, [0, 4d / 3d, 0, 0.5, 0, -1], 1);

        static double Spherical(double h)
            => h >= 4 ? 1 : (1.5 * (h / 4)) - (0.5 * Math.Pow(h / 4, 3));

        static double Predict(double x)
        {
            var difference = (Spherical(Math.Abs(x)) - Spherical(Math.Abs(4 - x))) / Spherical(4);
            var first = (1 - difference) / 2;
            return (first * 10) + ((1 - first) * 30);
        }

        double[] expected = [Predict(2d / 3d), Predict(2), Predict(10d / 3d)];
        // The midpoint of a symmetric two-sample configuration is the plain mean.
        expected[1].Should().Be(20);

        var values = output.GetProperty("bands")[0].GetProperty("values")
            .EnumerateArray().Select(v => v.GetDouble()).ToArray();
        values.Should().HaveCount(3);
        for (var i = 0; i < 3; i++)
        {
            values[i].Should().BeApproximately(expected[i], 1e-4, $"cell {i}");
        }
    }

    [Fact]
    public async Task Histogram_UnevenDistribution_ExcludesNoDataAndPreservesEveryBucket()
    {
        using var json = JsonDocument.Parse(await Execute("raster.histogram", ("source", Input("histogram.tif"))));
        var bands = json.RootElement.GetProperty("bands");
        bands.GetArrayLength().Should().Be(1);
        var band = bands[0];
        band.GetProperty("band").GetInt32().Should().Be(1);
        band.GetProperty("min").GetDouble().Should().Be(-0.5);
        band.GetProperty("max").GetDouble().Should().Be(255.5);
        var expected = new int[256];
        expected[0] = 3;
        expected[1] = 2;
        expected[2] = 5;
        expected[3] = 1;
        var actual = band.GetProperty("buckets").EnumerateArray().Select(v => v.GetInt32()).ToArray();
        actual.Should().Equal(expected);
        actual.Sum().Should().Be(11);
    }

    [Fact]
    public async Task HistogramOracle_NoDataCountedAsData_IsRejected()
    {
        // A plausible wrong-but-well-formed histogram: the same cells over the same
        // 256 Byte buckets, but the sentinel is counted instead of excluded. Real
        // execution produces it from a source whose nodata declaration was dropped,
        // so the bucket bounds, band index and array shape all stay valid.
        Directory.CreateDirectory(_scratch);
        File.Copy(Fixture("histogram.tif"), Path.Join(_scratch, "histogram.tif"), overwrite: true);
        await Run("gdal_translate", ["-a_nodata", "none", "histogram.tif", "counted.tif"]);
        using var json = JsonDocument.Parse(await Execute("raster.histogram",
            ("source", Convert.ToBase64String(await File.ReadAllBytesAsync(Path.Join(_scratch, "counted.tif"))))));
        var band = json.RootElement.GetProperty("bands")[0];
        band.GetProperty("band").GetInt32().Should().Be(1);
        band.GetProperty("min").GetDouble().Should().Be(-0.5);
        band.GetProperty("max").GetDouble().Should().Be(255.5);
        var actual = band.GetProperty("buckets").EnumerateArray().Select(v => v.GetInt32()).ToArray();
        actual.Should().HaveCount(256);
        actual[255].Should().Be(1, "the dropped sentinel is now counted as a measurement");
        actual.Sum().Should().Be(12);
        // The frozen distribution oracle used by the proof above rejects it.
        var expected = new int[256];
        expected[0] = 3;
        expected[1] = 2;
        expected[2] = 5;
        expected[3] = 1;
        Action assert = () => actual.Should().Equal(expected);
        assert.Should().Throw<XunitException>();
    }

    [Fact]
    public async Task ZonalOracle_GlobalStatisticsSubstitutedForZoneSelection_IsRejected()
    {
        // A plausible wrong-but-well-formed zonal result: one row carrying the
        // requested zone id and valid statistics, but aggregated over the whole
        // raster instead of the zone's cells. Real execution produces it from a
        // zone polygon spanning the full extent.
        const string global = "{\"type\":\"FeatureCollection\",\"features\":[{\"type\":\"Feature\"," +
            "\"properties\":{\"id\":\"left\"},\"geometry\":{\"type\":\"Polygon\"," +
            "\"coordinates\":[[[0,0],[4,0],[4,4],[0,4],[0,0]]]}}]}";
        using var json = JsonDocument.Parse(await Execute("raster.zonal-statistics",
            ("source", Input("grid.tif")),
            ("zones", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(global))),
            ("band", "2")));
        var zone = json.RootElement.GetProperty("zones").EnumerateArray().Should().ContainSingle().Which;
        // Band two holds 10..160 by tens with 60 replaced by nodata: 15 valid cells summing to 1300.
        AssertZone(zone, "left", 15, 10, 160, 1300);
        // The hand-derived left-zone oracle ({10,20,50}) rejects the substitution.
        Action assert = () => AssertZone(zone, "left", 3, 10, 50, 80);
        assert.Should().Throw<XunitException>();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Reclassify_SingleValuesRangesGapsAndNoData_MatchesEveryClass(bool useDefault, bool overrideNoData)
    {
        var inputs = new List<(string, string)>
        {
            ("source", Input("reclassify.tif")),
            ("remap", "-2:7;0..2:10;2:20;2..5:30;6..10:40;-9999:99"),
            ("dataType", "Int16")
        };
        if (useDefault)
        {
            inputs.Add(("defaultValue", "-7"));
        }
        if (overrideNoData)
        {
            inputs.Add(("noData", "-1234"));
        }
        var output = await ExecuteRaster("raster.reclassify", inputs.ToArray());
        AssertGrid(output, 4, 3, 4326, [10, 0.25, 0, 20, 0, -0.5], 1);
        // Ranges are [lo, hi); the first matching entry wins at the overlap 2.
        // Source nodata is masked even when explicitly listed in the remap.
        var nodata = overrideNoData ? -1234 : NoData;
        AssertBand(output, 0, [7, 10, 10, 20, 30, 30, useDefault ? -7 : 5,
            40, 40, useDefault ? -7 : 10, nodata, useDefault ? -7 : 11], "Int16", nodata);
    }

    [Theory]
    [InlineData("A + 2*B", false)]
    [InlineData("A/B", false)]
    [InlineData("A/B", true)]
    public async Task MapAlgebra_AlignedSources_MatchesArithmeticAndInvalidCellMask(string expression, bool overrideNoData)
    {
        var inputs = new List<(string, string)>
        {
            ("sources", Input("algebra-a.tif") + "|" + Input("algebra-b.tif")),
            ("expression", expression), ("dataType", "Float64")
        };
        if (overrideNoData)
        {
            inputs.Add(("noData", "-1234"));
        }
        var output = await ExecuteRaster("raster.map-algebra", inputs.ToArray());
        AssertGrid(output, 4, 2, 4326, [10, 0.25, 0, 20, 0, -0.5], 1);
        var n = overrideNoData ? -1234 : NoData;
        // A={2,4,0,n,6,-2,8,0}; B={1,0,0,2,n,-4,2,5}.
        // A/B includes both nonzero/zero and zero/zero; neither is valid data.
        double[] expected = expression == "A/B" ? [2, n, n, n, n, 0.5, 4, 0] : [4, 4, 0, n, n, -10, 12, 10];
        AssertBand(output, 0, expected, "Float64", n);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Statistics_TwoBandsWithDifferentNoData_MatchesHandDerivedPopulationMoments(bool secondBandOnly)
    {
        var inputs = new List<(string, string)> { ("source", Input("statistics.tif")) };
        if (secondBandOnly)
        {
            inputs.Add(("bands", "2"));
        }
        using var json = JsonDocument.Parse(await Execute("raster.statistics", inputs.ToArray()));
        json.RootElement.GetProperty("kind").GetString().Should().Be("raster.statistics");
        var bands = json.RootElement.GetProperty("bands");
        bands.GetArrayLength().Should().Be(secondBandOnly ? 1 : 2);
        if (!secondBandOnly)
        {
            // {0,1,2,3,4}: n=5, sum=10, sum of squared deviations=10.
            AssertStatistics(bands[0], 1, 5, 0, 4, 2, Math.Sqrt(2));
        }
        // {-5,5,15,25}: n=4, sum=40, sum of squared deviations=500.
        AssertStatistics(bands[secondBandOnly ? 0 : 1], 2, 4, -5, 25, 10, Math.Sqrt(125));
    }

    [Theory]
    [InlineData("Float64", false)]
    [InlineData("Float64", true)]
    [InlineData("Int16", false)]
    public async Task MapAlgebra_FirstSourceWithoutNoData_UndefinedDivisionUsesTypeDefault(string type, bool secondSourceHasNoData)
    {
        var output = await ExecuteRaster("raster.map-algebra",
            ("sources", Input("algebra-a-unmasked.tif") + "|" + Input(secondSourceHasNoData ? "algebra-b.tif" : "algebra-b-unmasked.tif")),
            ("expression", "A/B"), ("dataType", type));
        AssertGrid(output, 4, 2, 4326, [10, 0.25, 0, 20, 0, -0.5], 1);
        // GDAL defaults are Float64.MaxValue and Int16.MinValue. Integer
        // RasterIO rounds the exactly representable half to the nearest integer.
        var n = type == "Int16" ? short.MinValue : double.MaxValue;
        AssertBand(output, 0, [2, n, n, 5, secondSourceHasNoData ? n : 2, type == "Int16" ? 1 : 0.5, 4, 0], type, n);
    }

    [Fact]
    public async Task Statistics_MultipleReadWindows_CountsExactlyWhenValidPercentRoundsTo100()
    {
        using var json = JsonDocument.Parse(await Execute("raster.statistics", ("source", Input("statistics-wide.tif"))));
        var bands = json.RootElement.GetProperty("bands");
        bands.GetArrayLength().Should().Be(1);
        // A 513x513 plane of ones with one nodata at (256,256) spans nine
        // read windows. Its valid percentage rounds to 100, but count != area.
        AssertStatistics(bands[0], 1, 513 * 513 - 1, 1, 1, 1, 0);
    }

    [Fact]
    public async Task Statistics_ExplicitValidityMask_ExcludesMaskedCellsAndBandNoData()
    {
        using var json = JsonDocument.Parse(await Execute("raster.statistics", ("source", Input("statistics-masked.tif"))));
        var bands = json.RootElement.GetProperty("bands");
        bands.GetArrayLength().Should().Be(2);
        // The explicit mask removes the first sample in each band in addition
        // to band nodata: {1,2,3,4} and {5,15,25}, with population variances 5/4 and 200/3.
        AssertStatistics(bands[0], 1, 4, 1, 4, 2.5, Math.Sqrt(1.25));
        AssertStatistics(bands[1], 2, 3, 5, 25, 15, Math.Sqrt(200.0 / 3));
    }

    [Fact]
    public async Task ReclassifyOracle_InclusiveUpperBoundClasses_IsRejected()
    {
        // A plausible wrong-but-well-formed reclassification: the same source, the
        // same table shape and the same Int16 output, but range keys read as closed
        // [lo, hi] instead of half-open [lo, hi). Every source sample is an integer,
        // so executing the proof's own table with each upper bound raised short of
        // the next integer reproduces exactly what an inclusive implementation
        // publishes for the proof's table - real execution, no fabricated raster.
        var output = await ExecuteRaster("raster.reclassify",
            ("source", Input("reclassify.tif")),
            ("remap", "-2:7;0..2.5:10;2:20;2..5.5:30;6..10.5:40;-9999:99"),
            ("dataType", "Int16"));
        AssertGrid(output, 4, 3, 4326, [10, 0.25, 0, 20, 0, -0.5], 1);
        // Source {-2,0,1,2,3,4,5,6,9,10,nodata,11}. The boundary samples 2, 5 and 10
        // are swallowed by the widened ranges: 2 no longer reaches the later 2:20
        // entry, and 5 and 10 are no longer unmatched, so they stop being preserved.
        AssertBand(output, 0, [7, 10, 10, 10, 30, 30, 30, 40, 40, 40, NoData, 11], "Int16", NoData);
        // The hand-derived class oracle rejects the substitution.
        Action assert = () => AssertBand(output, 0,
            [7, 10, 10, 20, 30, 30, 5, 40, 40, 10, NoData, 11], "Int16", NoData);
        assert.Should().Throw<XunitException>();
    }

    [Fact]
    public async Task ReclassifyOracle_SourceNoDataRemappedAsData_IsRejected()
    {
        // A plausible wrong-but-well-formed reclassification: the sentinel is
        // classified like any other sample instead of staying masked. Real execution
        // produces it from a source whose nodata declaration was dropped, so the
        // class table, Int16 type, grid and every other cell stay valid.
        Directory.CreateDirectory(_scratch);
        File.Copy(Fixture("reclassify.tif"), Path.Join(_scratch, "reclassify.tif"), overwrite: true);
        await Run("gdal_translate", ["-a_nodata", "none", "reclassify.tif", "declared.tif"]);
        // The caller still requests the proof's sentinel, so the output declares the
        // same nodata value and the same Int16 type as the proof's own result. Only
        // the validity mask and the sentinel cell's class differ, which is what makes
        // the mask assertion - not a metadata mismatch - the thing that rejects this.
        var output = await ExecuteRaster("raster.reclassify",
            ("source", Convert.ToBase64String(await File.ReadAllBytesAsync(Path.Join(_scratch, "declared.tif")))),
            ("remap", "-2:7;0..2:10;2:20;2..5:30;6..10:40;-9999:99"),
            ("dataType", "Int16"), ("noData", "-9999"));
        AssertGrid(output, 4, 3, 4326, [10, 0.25, 0, 20, 0, -0.5], 1);
        // The dropped declaration lets the sentinel match its own -9999:99 entry, so
        // cell 10 publishes class 99 and every cell is valid. No cell takes -9999.
        AssertBand(output, 0, [7, 10, 10, 20, 30, 30, 5, 40, 40, 10, 99, 11], "Int16", NoData,
            mask: [255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255]);
        // The hand-derived oracle, which requires the sentinel to survive as nodata
        // even though the remap lists it, rejects the substitution on the validity
        // mask: it expects cell 10 masked, and the wrong result marks it valid.
        Action assert = () => AssertBand(output, 0,
            [7, 10, 10, 20, 30, 30, 5, 40, 40, 10, NoData, 11], "Int16", NoData);
        assert.Should().Throw<XunitException>();
    }

    [Fact]
    public async Task MapAlgebraOracle_SourceNoDataTreatedAsData_IsRejected()
    {
        // A plausible wrong-but-well-formed algebra result: the same expression over
        // the same cells, but each source's masked sample is measured instead of
        // propagated. Real execution produces it from the committed unmasked
        // fixtures, so the grid, Float64 type and declared sentinel stay valid.
        // The caller still requests the proof's sentinel, so the output declares the
        // same nodata value, type and grid as the proof's own result. Only the
        // validity mask and the two recovered cells differ, which is what makes the
        // mask assertion - not a metadata mismatch - the thing that rejects this.
        var output = await ExecuteRaster("raster.map-algebra",
            ("sources", Input("algebra-a-unmasked.tif") + "|" + Input("algebra-b-unmasked.tif")),
            ("expression", "A + 2*B"), ("dataType", "Float64"), ("noData", "-9999"));
        AssertGrid(output, 4, 2, 4326, [10, 0.25, 0, 20, 0, -0.5], 1);
        // A={2,4,0,10,6,-2,8,0} and B={1,0,0,2,3,-4,2,5} carry no mask, so every cell
        // is valid and the two formerly masked positions publish 10+2*2 and 6+2*3.
        AssertBand(output, 0, [4, 4, 0, 14, 12, -10, 12, 10], "Float64", NoData,
            mask: [255, 255, 255, 255, 255, 255, 255, 255]);
        // The hand-derived oracle, which requires the union of the input nodata masks,
        // rejects the substitution on the validity mask: it expects cells 3 and 4
        // masked, and the wrong result marks them valid.
        Action assert = () => AssertBand(output, 0,
            [4, 4, 0, NoData, NoData, -10, 12, 10], "Float64", NoData);
        assert.Should().Throw<XunitException>();
    }

    [Fact]
    public async Task MapAlgebraOracle_MisassociatedExpression_IsRejected()
    {
        // A plausible wrong-but-well-formed algebra result: the caller expression is
        // grouped as (A+2)*B instead of A+2*B, the canonical precedence defect for an
        // expression evaluator. Real execution over the masked sources keeps the grid,
        // Float64 type, declared sentinel AND the union-of-masks validity identical to
        // the proof's result, so nothing but the computed cells can reject it.
        var output = await ExecuteRaster("raster.map-algebra",
            ("sources", Input("algebra-a.tif") + "|" + Input("algebra-b.tif")),
            ("expression", "(A+2)*B"), ("dataType", "Float64"));
        AssertGrid(output, 4, 2, 4326, [10, 0.25, 0, 20, 0, -0.5], 1);
        // A={2,4,0,nodata,6,-2,8,0}, B={1,0,0,2,nodata,-4,2,5}: (A+2)*B differs from
        // A+2*B at cells 1, 5 and 6 while every masked position stays masked.
        AssertBand(output, 0, [4, 0, 0, NoData, NoData, 0, 20, 10], "Float64", NoData);
        // The hand-derived arithmetic oracle rejects it on the decoded cell values.
        Action assert = () => AssertBand(output, 0,
            [4, 4, 0, NoData, NoData, -10, 12, 10], "Float64", NoData);
        assert.Should().Throw<XunitException>();
    }

    [Fact]
    public async Task StatisticsOracle_NoDataCountedAsData_IsRejected()
    {
        // A plausible wrong-but-well-formed statistics document: the same two bands
        // over the same cells, but the sentinel is measured instead of excluded.
        // Real execution produces it from a source whose nodata declaration was
        // dropped, so band identities, counts and moments all stay well-formed.
        Directory.CreateDirectory(_scratch);
        File.Copy(Fixture("statistics.tif"), Path.Join(_scratch, "statistics.tif"), overwrite: true);
        await Run("gdal_translate", ["-a_nodata", "none", "statistics.tif", "counted.tif"]);
        using var json = JsonDocument.Parse(await Execute("raster.statistics",
            ("source", Convert.ToBase64String(await File.ReadAllBytesAsync(Path.Join(_scratch, "counted.tif"))))));
        var bands = json.RootElement.GetProperty("bands");
        bands.GetArrayLength().Should().Be(2);
        // Every cell becomes a measurement: {0,1,2,3,4,-9999} and {-5,-9999,5,-9999,15,25}.
        // Both counts reach the 6-cell area and both moments are dragged to the sentinel.
        AssertCountedPopulation(bands[0], 1, [0, 1, 2, 3, 4, NoData]);
        AssertCountedPopulation(bands[1], 2, [-5, NoData, 5, NoData, 15, 25]);
        // The hand-derived valid-population oracles reject both bands on validCount,
        // which is the exact-count assertion #4407 added. The moment assertions are
        // demonstrated separately, by a wrong result whose counts already agree, in
        // StatisticsOracle_QuantizedSourceMatchesCountsButNotMoments_IsRejected.
        Action first = () => AssertStatistics(bands[0], 1, 5, 0, 4, 2, Math.Sqrt(2));
        first.Should().Throw<XunitException>();
        Action second = () => AssertStatistics(bands[1], 2, 4, -5, 25, 10, Math.Sqrt(125));
        second.Should().Throw<XunitException>();
    }

    [Fact]
    public async Task StatisticsOracle_QuantizedSourceMatchesCountsButNotMoments_IsRejected()
    {
        // A plausible wrong-but-well-formed statistics document: the moments are
        // measured over a quantized derivative of the band - the shape a scalar
        // summary takes when it is computed from a classified or rendered product
        // instead of the measurement band - rather than over the source samples.
        // Real execution keeps band identity, Float32 type, the declared sentinel,
        // the exact valid count AND both extrema equal to the proof's result, so
        // only the mean and standard deviation can reject it.
        Directory.CreateDirectory(_scratch);
        File.Copy(Fixture("statistics.tif"), Path.Join(_scratch, "statistics.tif"), overwrite: true);
        await Run("gdal_calc.py", ["--calc", "where(A<2,0,4)", "-A", "statistics.tif", "--A_band", "1",
            "--type", "Float32", "--NoDataValue=-9999", "--overwrite", "--quiet", "--outfile", "quantized.tif"]);
        using var json = JsonDocument.Parse(await Execute("raster.statistics",
            ("source", Convert.ToBase64String(await File.ReadAllBytesAsync(Path.Join(_scratch, "quantized.tif"))))));
        var bands = json.RootElement.GetProperty("bands");
        bands.GetArrayLength().Should().Be(1);
        // Band 1 {0,1,2,3,4} collapses to {0,0,4,4,4}: nodata still masked, so the
        // count stays 5 and the extrema stay 0 and 4, but the distribution moves.
        // n=5, sum=12, sum of squared deviations=19.2.
        AssertCountedPopulation(bands[0], 1, [0, 0, 4, 4, 4], NoData);
        // The hand-derived moment oracle rejects it: count, nodata, min and max all
        // agree, so the mean assertion (2 against 2.4) is what fails.
        Action assert = () => AssertStatistics(bands[0], 1, 5, 0, 4, 2, Math.Sqrt(2));
        assert.Should().Throw<XunitException>();
    }

    /// <summary>
    /// Independently derives count, extrema, mean and population standard deviation
    /// in C# from the cells a wrong implementation would measure, so a demonstrated
    /// negative states its own result rather than snapshotting whatever GDAL printed.
    /// </summary>
    private static void AssertCountedPopulation(JsonElement band, int index, double[] population, double? noData = null)
    {
        band.GetProperty("band").GetInt32().Should().Be(index);
        band.GetProperty("type").GetString().Should().Be("Float32");
        band.GetProperty("validCount").GetInt64().Should().Be(population.Length);
        if (noData is null)
        {
            band.GetProperty("noDataValue").ValueKind.Should().Be(JsonValueKind.Null,
                "the dropped declaration is what makes the sentinel a measurement");
        }
        else
        {
            band.GetProperty("noDataValue").GetDouble().Should().Be(noData.Value);
        }
        var mean = population.Average();
        var variance = population.Select(v => (v - mean) * (v - mean)).Sum() / population.Length;
        band.GetProperty("min").GetDouble().Should().Be(population.Min());
        band.GetProperty("max").GetDouble().Should().Be(population.Max());
        // Sentinel-dragged moments are ~1e3 in magnitude, so the shared bounded
        // reader's accumulation order is pinned relatively, not to the 1e-12 used
        // for the single-digit valid populations.
        band.GetProperty("mean").GetDouble().Should().BeApproximately(mean, 1e-6);
        band.GetProperty("stddev").GetDouble().Should().BeApproximately(Math.Sqrt(variance), 1e-6);
    }

    private static void AssertStatistics(JsonElement band, int index, long count, double min, double max, double mean, double stddev)
    {
        band.GetProperty("band").GetInt32().Should().Be(index);
        band.GetProperty("type").GetString().Should().Be("Float32");
        band.GetProperty("validCount").GetInt64().Should().Be(count);
        band.GetProperty("noDataValue").GetDouble().Should().Be(NoData);
        band.GetProperty("min").GetDouble().Should().Be(min);
        band.GetProperty("max").GetDouble().Should().Be(max);
        band.GetProperty("mean").GetDouble().Should().BeApproximately(mean, 1e-12);
        band.GetProperty("stddev").GetDouble().Should().BeApproximately(stddev, 1e-12);
    }

    private static double Sample(double x, double y, string mode)
    {
        var centerX = Math.Clamp((int)Math.Floor(x + 0.5), 0, 3);
        var centerY = Math.Clamp((int)Math.Floor(y + 0.5), 0, 3);
        var center = Grid[centerY * 4 + centerX];
        if (mode == "nearest" || center == NoData)
        {
            return center;
        }
        double sum = 0, weight = 0;
        for (var row = 0; row < 4; row++)
        {
            for (var col = 0; col < 4; col++)
            {
                var w = Math.Max(0, 1 - Math.Abs(x - col)) * Math.Max(0, 1 - Math.Abs(y - row));
                if (Grid[row * 4 + col] != NoData)
                {
                    sum += Grid[row * 4 + col] * w;
                    weight += w;
                }
            }
        }
        return sum / weight;
    }

    private static void AssertZone(JsonElement zone, string id, int count, double min, double max, double sum)
    {
        zone.GetProperty("zoneId").GetString().Should().Be(id);
        zone.GetProperty("count").GetDouble().Should().Be(count);
        zone.GetProperty("min").GetDouble().Should().Be(min);
        zone.GetProperty("max").GetDouble().Should().Be(max);
        zone.GetProperty("sum").GetDouble().Should().BeApproximately(sum, 1e-9);
        zone.GetProperty("mean").GetDouble().Should().BeApproximately(sum / count, 1e-9);
    }

    private static void AssertGrid(JsonElement raster, int width, int height, int srid, double[] transform, int bands, double tolerance = 1e-9)
    {
        raster.GetProperty("width").GetInt32().Should().Be(width);
        raster.GetProperty("height").GetInt32().Should().Be(height);
        raster.GetProperty("srid").GetInt32().Should().Be(srid);
        raster.GetProperty("bands").GetArrayLength().Should().Be(bands);
        var actual = raster.GetProperty("transform").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        actual.Should().HaveCount(6);
        for (var i = 0; i < 6; i++)
        {
            actual[i].Should().BeApproximately(transform[i], tolerance, $"geotransform ordinate {i}");
        }
    }

    private static void AssertBand(JsonElement raster, int bandIndex, double[] expected, string type = "Float32", double? nodata = NoData, double tolerance = 1e-9, int[]? mask = null)
    {
        var band = raster.GetProperty("bands")[bandIndex];
        band.GetProperty("type").GetString().Should().Be(type);
        if (nodata is null)
        {
            band.GetProperty("nodata").ValueKind.Should().Be(JsonValueKind.Null);
        }
        else if (double.IsNaN(nodata.Value))
        {
            band.GetProperty("nodata").GetString().Should().Be("nan");
        }
        else
        {
            band.GetProperty("nodata").ValueKind.Should().Be(JsonValueKind.Number, "the output must declare its nodata sentinel");
            band.GetProperty("nodata").GetDouble().Should().Be(nodata.Value);
        }
        var values = band.GetProperty("values").EnumerateArray().ToArray();
        var actualMask = band.GetProperty("mask").EnumerateArray().Select(v => v.GetInt32()).ToArray();
        var expectedMask = mask ?? expected.Select(v => double.IsNaN(v) || v == nodata ? 0 : 255).ToArray();
        actualMask.Should().Equal(expectedMask, $"band {bandIndex + 1} validity must match the analytical nodata mask");
        values.Should().HaveCount(expected.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            if (double.IsNaN(expected[i]))
            {
                values[i].GetString().Should().Be("nan", $"band {bandIndex + 1}, cell {i}");
            }
            else
            {
                values[i].GetDouble().Should().BeApproximately(expected[i], tolerance, $"band {bandIndex + 1}, cell {i}");
            }
        }
    }

    private async Task<JsonElement> ExecuteRaster(string id, params (string, string)[] inputs) => await Decode(await Execute(id, inputs));

    private async Task<byte[]> Execute(string id, params (string, string)[] inputs)
    {
        var options = GdalJobFactory.Options(_scratch);
        IProcessExecutor executor = id switch
        {
            "gdal.gdalwarp" => new GdalRasterReprojectJobExecutor(_runner, options, NullLogger<GdalRasterReprojectJobExecutor>.Instance),
            "source.ogr" => new GdalVectorSourceReadJobExecutor(_runner, options, NullLogger<GdalVectorSourceReadJobExecutor>.Instance),
            "surface.viewshed" => new GdalViewshedJobExecutor(_runner, options, NullLogger<GdalViewshedJobExecutor>.Instance),
            "surface.contour" => new GdalContourJobExecutor(_runner, options, NullLogger<GdalContourJobExecutor>.Instance),
            _ when id.StartsWith("surface.", StringComparison.Ordinal) => new GdalSurfaceJobExecutor(_runner, options, NullLogger<GdalSurfaceJobExecutor>.Instance),
            "raster.clip" => new GdalRasterClipJobExecutor(_runner, options, NullLogger<GdalRasterClipJobExecutor>.Instance),
            "raster.zonal-statistics" => new GdalRasterZonalStatisticsJobExecutor(_runner, options, NullLogger<GdalRasterZonalStatisticsJobExecutor>.Instance),
            "raster.spectral-index" => new GdalRasterSpectralIndexJobExecutor(_runner, options, NullLogger<GdalRasterSpectralIndexJobExecutor>.Instance),
            "raster.reproject" or "conversion.raster-reproject" => new GdalRasterReprojectCatalogJobExecutor(_runner, options, NullLogger<GdalRasterReprojectCatalogJobExecutor>.Instance),
            "conversion.raster-format" => new GdalRasterFormatConvertJobExecutor(_runner, options, NullLogger<GdalRasterFormatConvertJobExecutor>.Instance),
            "conversion.polygonize" => new GdalPolygonizeJobExecutor(_runner, options, NullLogger<GdalPolygonizeJobExecutor>.Instance),
            "raster.mosaic" => new GdalRasterMosaicJobExecutor(_runner, options, NullLogger<GdalRasterMosaicJobExecutor>.Instance),
            "raster.resample" => new GdalRasterResampleJobExecutor(_runner, options, NullLogger<GdalRasterResampleJobExecutor>.Instance),
            "raster.interpolate-idw" or "raster.interpolate-kriging" =>
                new GdalRasterInterpolateJobExecutor(_runner, options, NullLogger<GdalRasterInterpolateJobExecutor>.Instance),
            "raster.histogram" => new GdalRasterStatisticsJobExecutor(_runner, options, NullLogger<GdalRasterStatisticsJobExecutor>.Instance),
            "conversion.rasterize" => new GdalRasterizeJobExecutor(_runner, options, NullLogger<GdalRasterizeJobExecutor>.Instance),
            "raster.statistics" => new GdalRasterStatisticsJobExecutor(_runner, options, NullLogger<GdalRasterStatisticsJobExecutor>.Instance),
            "raster.reclassify" => new GdalRasterReclassifyJobExecutor(_runner, options, NullLogger<GdalRasterReclassifyJobExecutor>.Instance),
            "raster.map-algebra" => new GdalRasterMapAlgebraJobExecutor(_runner, options, NullLogger<GdalRasterMapAlgebraJobExecutor>.Instance),
            _ => throw new ArgumentOutOfRangeException(nameof(id))
        };
        var job = GdalJobFactory.Job(id, inputs);
        var context = new RecordingJobExecutionContext(job.OperationId);
        var result = await executor.ExecuteAsync(job, context, CancellationToken.None);
        result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
        context.Artifacts.Should().ContainSingle();
        return GdalCli.DecodeDataUri(context.Artifacts[0]);
    }

    private async Task<JsonElement> Decode(byte[] bytes)
    {
        Directory.CreateDirectory(_scratch);
        await File.WriteAllBytesAsync(Path.Join(_scratch, "decoded.tif"), bytes);
        File.Copy(Fixture("decode.py"), Path.Join(_scratch, "decode.py"), overwrite: true);
        using var json = JsonDocument.Parse(await Run("python3", ["decode.py", "decoded.tif"]));
        return json.RootElement.Clone();
    }

    private async Task<string> Run(string tool, string[] args)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var result = await _runner.RunAsync(tool, args, _scratch, timeout.Token);
        result.ExitCode.Should().Be(0, result.StandardError);
        return result.StandardOutput;
    }

    private static string Fixture(string name) => Path.Join(AppContext.BaseDirectory, "Fixtures", "RasterProof", name);
    private static string Input(string name) => Convert.ToBase64String(File.ReadAllBytes(Fixture(name)));
    public void Dispose() => GdalCli.CleanupScratch(_scratch);

}
