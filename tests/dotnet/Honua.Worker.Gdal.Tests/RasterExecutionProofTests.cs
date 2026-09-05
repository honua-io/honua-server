// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Worker.Gdal.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.IO;
using Xunit;

namespace Honua.Worker.Gdal.Tests;

/// <summary>Real production executors and pinned native GDAL, with analytical cell oracles.</summary>
[Trait("Category", "RasterExecutionProof")]
public sealed class RasterExecutionProofTests : IDisposable
{
    private const double NoData = -9999;
    private const string Image = "ghcr.io/osgeo/gdal:ubuntu-full-3.13.1@sha256:aff1d5515aa0e9b50be34ab11d6c0c2cfabc23cdcb7a2e0bc5748101eedb3e4a";
    private static readonly double[] Grid = [1, 2, 3, 4, 5, NoData, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
    private readonly string _scratch = Path.Join(AppContext.BaseDirectory, "raster-proof", Guid.NewGuid().ToString("N"));
    private readonly DockerGdalCommandRunner _runner = new(
        new PortableDockerInvoker(),
        Options.Create(new GdalContainerExecutionOptions { Image = Image }),
        Options.Create(new GdalHardeningOptions()), Options.Create(new AwsS3Options()),
        Options.Create(new AzureBlobOptions()), NullLogger<DockerGdalCommandRunner>.Instance);

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

    [Theory]
    [InlineData("ndvi")]
    [InlineData("evi")]
    public async Task SpectralIndex_ReflectanceBands_MatchFormulaIncludingUndefinedAndNoData(string index)
    {
        Directory.CreateDirectory(_scratch);
        File.Copy(Fixture("reflectance.tif"), Path.Join(_scratch, "reflectance.tif"));
        var inputs = new List<(string, string)> { ("index", index) };
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
        var expected = Enumerable.Range(0, 8).Select(i => red[i] == NoData || nir[i] == NoData ? NoData
            : index == "ndvi" ? (nir[i] - red[i]) / (nir[i] + red[i])
            : 2.5 * (nir[i] - red[i]) / (nir[i] + 6 * red[i] - 7.5 * blue[i] + 1)).ToArray();
        AssertBand(output, 0, expected, tolerance: 1e-6);
    }

    [Theory]
    [InlineData("nearest")]
    [InlineData("bilinear")]
    public async Task Reproject_GeographicToMercator_MatchesAnalyticalGridAndInverseMappedSamples(string resampling)
    {
        var source = await Decode(await File.ReadAllBytesAsync(Fixture("grid.tif")));
        AssertGrid(source, 4, 4, 4326, [0, 1, 0, 4, 0, -1], 2);
        AssertBand(source, 0, Grid);
        var output = await ExecuteRaster("raster.reproject", ("source", Input("grid.tif")),
            ("targetSrid", "3857"), ("resampling", resampling));
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
        AssertBand(output, 0, expected, "Float64", double.NaN, 1e-6);
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

    private static void AssertBand(JsonElement raster, int bandIndex, double[] expected, string type = "Float32", double nodata = NoData, double tolerance = 1e-9)
    {
        var band = raster.GetProperty("bands")[bandIndex];
        band.GetProperty("type").GetString().Should().Be(type);
        if (double.IsNaN(nodata))
        {
            band.GetProperty("nodata").GetString().Should().Be("nan");
        }
        else
        {
            band.GetProperty("nodata").GetDouble().Should().Be(nodata);
        }
        var values = band.GetProperty("values").EnumerateArray().ToArray();
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
            "raster.clip" => new GdalRasterClipJobExecutor(_runner, options, NullLogger<GdalRasterClipJobExecutor>.Instance),
            "raster.zonal-statistics" => new GdalRasterZonalStatisticsJobExecutor(_runner, options, NullLogger<GdalRasterZonalStatisticsJobExecutor>.Instance),
            "raster.spectral-index" => new GdalRasterSpectralIndexJobExecutor(_runner, options, NullLogger<GdalRasterSpectralIndexJobExecutor>.Instance),
            "raster.reproject" => new GdalRasterReprojectCatalogJobExecutor(_runner, options, NullLogger<GdalRasterReprojectCatalogJobExecutor>.Instance),
            "raster.mosaic" => new GdalRasterMosaicJobExecutor(_runner, options, NullLogger<GdalRasterMosaicJobExecutor>.Instance),
            "raster.resample" => new GdalRasterResampleJobExecutor(_runner, options, NullLogger<GdalRasterResampleJobExecutor>.Instance),
            "raster.interpolate-idw" => new GdalRasterInterpolateJobExecutor(_runner, options, NullLogger<GdalRasterInterpolateJobExecutor>.Instance),
            "raster.histogram" => new GdalRasterStatisticsJobExecutor(_runner, options, NullLogger<GdalRasterStatisticsJobExecutor>.Instance),
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

    // Only translate host paths at the Docker transport boundary. The production
    // runner still builds the command, applies hardening and invokes real tools.
    // This permits Windows dotnet + Docker Desktop without a Linux build host.
    private sealed class PortableDockerInvoker : IDockerCommandInvoker
    {
        private readonly ProcessDockerCommandInvoker _inner = new(NullLogger<ProcessDockerCommandInvoker>.Instance);

        public Task<GdalCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken)
        {
            var args = arguments.ToArray();
            var mountIndex = Array.IndexOf(args, "-v") + 1;
            var workIndex = Array.IndexOf(args, "-w") + 1;
            var workspace = args[workIndex];
            for (var i = workIndex; i < args.Length; i++)
            {
                args[i] = args[i].Replace(workspace, "/proof", StringComparison.Ordinal).Replace('\\', '/');
            }
            args[mountIndex] = workspace + ":/proof";
            return _inner.RunAsync(executable, args, environment, cancellationToken);
        }

        public Task<bool> ImageExistsAsync(string executable, string image, CancellationToken cancellationToken)
            => _inner.ImageExistsAsync(executable, image, cancellationToken);
    }
}
