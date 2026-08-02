// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Worker.Gdal.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Honua.Worker.Gdal.Tests;

public sealed class RasterStagingJobExecutionContextTests : IDisposable
{
    private readonly string _root = Path.Join(
        Path.GetTempPath(),
        "honua-raster-staging-context-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PublishFileArtifactAsync_StagesCogAboveInlineCeiling_AndPublishesOnlyManifestMarker()
    {
        Directory.CreateDirectory(_root);
        var sourcePath = Path.Join(_root, "large-output.tif");
        var bytes = new byte[1024 * 1024 + 17];
        Random.Shared.NextBytes(bytes);
        await File.WriteAllBytesAsync(sourcePath, bytes);

        var runner = new FakeGdalCommandRunner((tool, arguments, _) => tool switch
        {
            "gdal_translate" => CopyAsSuccess(arguments[^2], arguments[^1]),
            "gdalinfo" when arguments.SequenceEqual(["--version"]) => new GdalCommandResult
            {
                ExitCode = 0,
                StandardOutput = "GDAL 3.10.0"
            },
            "gdalinfo" => new GdalCommandResult
            {
                ExitCode = 0,
                StandardOutput = """
                {
                  "size": [512, 256],
                  "coordinateSystem": { "wkt": "PROJCRS[\"WGS 84 / Pseudo-Mercator\",ID[\"EPSG\",3857]]" },
                  "geoTransform": [100, 10, 0, 200, 0, -10],
                  "bands": [{ "band": 1 }]
                }
                """
            },
            _ => new GdalCommandResult { ExitCode = 1 }
        });
        var store = new LocalRasterOutputObjectStore(_root, "gp-results");
        var inner = new RecordingJobExecutionContext("job-42");
        var job = GdalJobFactory.Job("raster.resample") with
        {
            OperationId = "job-42",
            AttemptCount = 2,
            Spec = GdalJobFactory.Job("raster.resample").Spec with
            {
                ContractVersion = RasterOutputContract.JobContractVersion,
                Parameters = new Dictionary<string, string>
                {
                    [GdalWorkerParameterKeys.ProcessDefinitions] = "raster.resample",
                    [RasterOutputWorkerContract.StoreReferenceParameter] = "gp-results"
                }
            }
        };

        await using var context = new RasterStagingJobExecutionContext(
            job,
            inner,
            store,
            store,
            runner,
            NullLogger.Instance,
            "gp-results");

        var result = await context.PublishFileArtifactAsync(
            sourcePath,
            "image/tiff",
            maximumInlineBytes: 64,
            "Large raster");

        result.Succeeded.Should().BeTrue();
        result.SizeBytes.Should().BeGreaterThan(64);
        inner.Artifacts.Should().ContainSingle()
            .Which.Should().StartWith("honua-raster-manifest:gp-results:");
        inner.Artifacts.Should().NotContain(reference => reference.StartsWith("data:", StringComparison.Ordinal));

        var manifestKey = RasterOutputWorkerContract.BuildManifestObjectKey("job-42", 2);
        var manifest = await store.ReadManifestAsync("gp-results", manifestKey);
        manifest.Should().NotBeNull();
        manifest!.Outputs.Should().ContainSingle();
        var output = manifest.Outputs[0];
        output.Encoding.Should().Be(RasterOutputEncoding.CloudOptimizedGeoTiff);
        output.Content.SizeBytes.Should().Be(bytes.LongLength);
        output.Content.Checksum.Should().NotBeNull();
        output.Grid.Crs.Should().Be("EPSG:3857");
        output.Grid.Width.Should().Be(512);
        output.Grid.Height.Should().Be(256);
        output.Engine.Should().Be(new RasterProducingEngine("gdal", "GDAL 3.10.0"));
        output.Lineage.JobId.Should().Be("job-42");
        output.Lineage.Attempt.Should().Be(2);
        output.Lineage.ProcessId.Should().Be("raster.resample");
        (await store.InspectAsync("gp-results", output.ObjectKey)).Should().NotBeNull();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static GdalCommandResult CopyAsSuccess(string source, string destination)
    {
        File.Copy(source, destination);
        return new GdalCommandResult { ExitCode = 0 };
    }
}
