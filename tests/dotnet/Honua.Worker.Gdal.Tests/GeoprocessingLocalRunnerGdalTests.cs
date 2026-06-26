// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Geoprocessing.LocalRunner;
using Honua.TestKit.Attributes;
using Honua.Worker.Gdal.Execution;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Worker.Gdal.Tests;

/// <summary>
/// Offline coverage for the headless <see cref="GeoprocessingLocalRunner"/> (GP Devkit,
/// issue #2123) driving a native GDAL op. The runner invokes the real
/// <c>gdal.ogr2ogr</c> executor with no Redis/queue/job-store; a
/// <see cref="FakeGdalCommandRunner"/> stands in for the <c>ogr2ogr</c> CLI so the run
/// is fully offline. This proves the GP Devkit contract for native ops: the run
/// returns the published artifact AND the structured logs that carry the actual GDAL
/// command line a developer would otherwise have to reconstruct by hand.
/// </summary>
public sealed class GeoprocessingLocalRunnerGdalTests
{
    private const string ScratchSuite = "honua-gdal-local-runner-test";

    private static readonly byte[] ConvertedBytes = Encoding.UTF8.GetBytes("id,wkt\n1,POINT(1 2)\n");

    [UnitTest]
    public async Task RunAsync_GdalVectorConvert_ReturnsArtifactAndSurfacesActualCommand()
    {
        var scratch = GdalCli.NewScratch(ScratchSuite);
        try
        {
            // ogr2ogr's arg order is `-f CSV <output> <input>`: the output path is the
            // second-to-last argument, so the fake writes its bytes there.
            var gdalRunner = FakeGdalCommandRunner.Succeeding(ConvertedBytes, outputArgIndexFromEnd: 1);
            var executor = new GdalVectorConvertJobExecutor(
                gdalRunner,
                GdalJobFactory.Options(scratch),
                NullLogger<GdalVectorConvertJobExecutor>.Instance);

            var runner = new GeoprocessingLocalRunner([executor]);

            var result = await runner.RunAsync(
                GdalVectorConvertJobExecutor.HandledProcessId,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["source"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                        """{"type":"FeatureCollection","features":[{"type":"Feature","geometry":{"type":"Point","coordinates":[1,2]},"properties":{"name":"a"}}]}""")),
                    ["sourceFormat"] = "GeoJSON",
                    ["targetFormat"] = "CSV",
                });

            result.Succeeded.Should().BeTrue(result.ErrorMessage);
            result.ProcessId.Should().Be(GdalVectorConvertJobExecutor.HandledProcessId);
            result.Artifacts.Should().ContainSingle();
            result.Artifacts[0].Should().StartWith("data:text/csv;base64,");

            // The runner ran the real executor: the fake CLI was invoked once with ogr2ogr.
            gdalRunner.Invocations.Should().ContainSingle();
            gdalRunner.Invocations[0].Tool.Should().Be("ogr2ogr");

            // The GP Devkit contract for GDAL ops: the actual command line is surfaced
            // through the structured logs (with the scratch workspace redacted).
            var commandEntry = result.Logs.Should().ContainSingle(
                e => e.Metadata != null && e.Metadata.ContainsKey(GdalCommandLog.CommandMetadataKey)).Subject;
            commandEntry.Metadata![GdalCommandLog.ToolMetadataKey].Should().Be("ogr2ogr");
            commandEntry.Metadata[GdalCommandLog.CommandMetadataKey].Should().StartWith("ogr2ogr ");
            commandEntry.Metadata[GdalCommandLog.CommandMetadataKey].Should().NotContain(scratch);
            commandEntry.Metadata[GdalCommandLog.CommandMetadataKey].Should().Contain("<scratch>");
        }
        finally
        {
            GdalCli.CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task RunAsync_GdalCommandFails_SurfacesFailedResult()
    {
        var scratch = GdalCli.NewScratch(ScratchSuite);
        try
        {
            var gdalRunner = FakeGdalCommandRunner.Failing(1, "ERROR 1: ogr2ogr blew up");
            var executor = new GdalVectorConvertJobExecutor(
                gdalRunner,
                GdalJobFactory.Options(scratch),
                NullLogger<GdalVectorConvertJobExecutor>.Instance);

            var runner = new GeoprocessingLocalRunner([executor]);

            var result = await runner.RunAsync(
                GdalVectorConvertJobExecutor.HandledProcessId,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["source"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                        """{"type":"FeatureCollection","features":[{"type":"Feature","geometry":{"type":"Point","coordinates":[1,2]},"properties":{"name":"a"}}]}""")),
                    ["sourceFormat"] = "GeoJSON",
                    ["targetFormat"] = "CSV",
                });

            result.Succeeded.Should().BeFalse();
            result.Status.Should().Be(Honua.Core.Features.ControlPlane.Domain.ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().NotBeNullOrEmpty();
            result.Artifacts.Should().BeEmpty();
        }
        finally
        {
            GdalCli.CleanupScratch(scratch);
        }
    }
}
