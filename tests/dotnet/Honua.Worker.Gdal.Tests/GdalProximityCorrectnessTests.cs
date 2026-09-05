// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Worker.Gdal.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Worker.Gdal.Tests;

/// <summary>Execution-content proofs for the catalog's native proximity operations.</summary>
public sealed class GdalProximityCorrectnessTests
{
    private const string FixtureName = "proximity-sources.tif";
    private const double InputNoData = -9999;

    // Decoding only: the oracle below uses coordinates and Pythagoras, never GDAL
    // proximity, SciPy's EDT, or a captured output from either implementation.
    private const string DecodeScript = """
        import json, sys
        from osgeo import gdal
        gdal.UseExceptions()
        ds = gdal.Open(sys.argv[1])
        band = ds.GetRasterBand(1)
        srs = ds.GetSpatialRef()
        srs.AutoIdentifyEPSG()
        print(json.dumps(dict(width=ds.RasterXSize, height=ds.RasterYSize,
            bands=ds.RasterCount, transform=ds.GetGeoTransform(),
            epsg=srs.GetAuthorityCode(None), unit=srs.GetLinearUnitsName(),
            type=gdal.GetDataTypeName(band.DataType), nodata=band.GetNoDataValue(),
            cells=band.ReadAsArray().tolist())))
        """;

    [GdalCliFact("gdal_proximity.py")]
    public async Task Distance_RealLabeledFixture_MatchesEuclideanCellOracle()
    {
        await ProveAsync(GdalProximityJobExecutor.DistanceProcessId);
    }

    [GdalCliFact("python3")]
    public async Task Allocation_RealLabeledFixture_MatchesNearestLabelOracle()
    {
        await ProveAsync(GdalProximityJobExecutor.AllocationProcessId);
    }

    private static async Task ProveAsync(string processId)
    {
        var scratch = GdalCli.NewScratch("honua-proximity-correctness");
        Directory.CreateDirectory(scratch);
        try
        {
            var runner = new ProcessGdalCommandRunner(
                Options.Create(new GdalHardeningOptions()),
                Options.Create(new AwsS3Options()),
                Options.Create(new AzureBlobOptions()),
                NullLogger<ProcessGdalCommandRunner>.Instance);
            var executor = new GdalProximityJobExecutor(
                runner, GdalJobFactory.Options(scratch), NullLogger<GdalProximityJobExecutor>.Instance);
            var fixturePath = Path.Join(AppContext.BaseDirectory, "Fixtures", "Proximity", FixtureName);
            using var fixture = await DecodeAsync(runner, fixturePath, scratch);
            AssertGrid(fixture.RootElement, "Int16", InputNoData);
            for (var y = 0; y < 5; y++)
            {
                for (var x = 0; x < 7; x++)
                {
                    var expected = (x, y) switch
                    {
                        (1, 1) => 7,
                        (5, 3) => 23,
                        (0, 4) => InputNoData,
                        _ => 0,
                    };
                    fixture.RootElement.GetProperty("cells")[y][x].GetDouble().Should().Be(expected);
                }
            }

            var source = Convert.ToBase64String(await File.ReadAllBytesAsync(fixturePath));
            var allocation = processId == GdalProximityJobExecutor.AllocationProcessId;
            // Same numeric cutoff in both units detects ignored/misprojected units.
            // 20 GEO / 2 PIXEL additionally checks inclusive axial boundaries.
            foreach (var (units, limit, onlySecond) in new (string Units, double? Limit, bool OnlySecond)[]
            {
                ("GEO", null, false),
                ("PIXEL", null, false),
                ("GEO", 20, false),
                ("PIXEL", 20, false),
                ("PIXEL", 2, false),
                ("GEO", null, true),
            })
            {
                var inputs = new List<(string Name, string Value)>
                {
                    ("source", source),
                    ("distUnits", units),
                };
                // GDAL's default nonzero target rule includes a nonzero nodata
                // sentinel. Explicit values selects the two intended categories.
                // Allocation's default must itself exclude the nodata sentinel.
                if (!allocation || onlySecond)
                {
                    inputs.Add(("values", onlySecond ? "23" : "7,23"));
                }
                if (limit.HasValue)
                {
                    inputs.Add(("maxDistance", limit.Value.ToString(CultureInfo.InvariantCulture)));
                }

                var job = GdalJobFactory.Job(processId, inputs.ToArray());
                var context = new RecordingJobExecutionContext(job.OperationId);
                var result = await executor.ExecuteAsync(job, context, CancellationToken.None);
                result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
                context.Artifacts.Should().ContainSingle();
                var outputPath = Path.Join(scratch, "decoded-output.tif");
                await File.WriteAllBytesAsync(outputPath, GdalCli.DecodeDataUri(context.Artifacts.Single()));
                using var output = await DecodeAsync(runner, outputPath, scratch);
                var noData = InputNoData;
                AssertGrid(output.RootElement, allocation ? "Int16" : "Float32", noData);

                for (var y = 0; y < 5; y++)
                {
                    for (var x = 0; x < 7; x++)
                    {
                        var firstSquared = ((x - 1) * (x - 1)) + ((y - 1) * (y - 1));
                        var secondSquared = ((x - 5) * (x - 5)) + ((y - 3) * (y - 3));
                        var nearestSquared = onlySecond ? secondSquared : Math.Min(firstSquared, secondSquared);
                        var distance = Math.Sqrt(nearestSquared) * (units == "GEO" ? 10 : 1);
                        // This fixture's ties (4,0), (3,2), (2,4) select the
                        // source in the lower column, label 7.
                        var label = !onlySecond && firstSquared <= secondSquared ? 7 : 23;
                        var expected = limit.HasValue && distance > limit.Value
                            ? noData : allocation ? label : distance;
                        var actual = output.RootElement.GetProperty("cells")[y][x].GetDouble();
                        var because = $"{processId} {units} max={limit} only23={onlySecond}, cell ({x},{y})";
                        if (allocation || expected == noData)
                        {
                            actual.Should().Be(expected, because);
                        }
                        else
                        {
                            actual.Should().BeApproximately(expected, 0.00001, because);
                        }
                    }
                }
            }
        }
        finally
        {
            GdalCli.CleanupScratch(scratch);
        }
    }

    private static void AssertGrid(JsonElement raster, string dataType, double noData)
    {
        raster.GetProperty("width").GetInt32().Should().Be(7);
        raster.GetProperty("height").GetInt32().Should().Be(5);
        raster.GetProperty("bands").GetInt32().Should().Be(1);
        raster.GetProperty("epsg").GetString().Should().Be("32604");
        raster.GetProperty("unit").GetString().Should().Be("metre");
        raster.GetProperty("transform").EnumerateArray().Select(value => value.GetDouble())
            .Should().Equal(500000, 10, 0, 2200000, 0, -10);
        raster.GetProperty("type").GetString().Should().Be(dataType);
        raster.GetProperty("nodata").ValueKind.Should().Be(JsonValueKind.Number,
            "the band must declare nodata so consumers can mask beyond-cutoff cells");
        raster.GetProperty("nodata").GetDouble().Should().Be(noData);
    }

    private static async Task<JsonDocument> DecodeAsync(
        ProcessGdalCommandRunner runner, string path, string scratch)
    {
        var decoded = await runner.RunAsync("python3", ["-c", DecodeScript, path], scratch, CancellationToken.None);
        decoded.Succeeded.Should().BeTrue(decoded.StandardError);
        return JsonDocument.Parse(decoded.StandardOutput);
    }
}
