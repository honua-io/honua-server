// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
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
    public async Task RunAsync_Default_StaysSanitizedAndOmitsGlassBox()
    {
        var scratch = GdalCli.NewScratch(ScratchSuite);
        try
        {
            var gdalRunner = FakeGdalCommandRunner.Succeeding(ConvertedBytes, outputArgIndexFromEnd: 1);
            var executor = new GdalVectorConvertJobExecutor(
                gdalRunner,
                GdalJobFactory.Options(scratch),
                NullLogger<GdalVectorConvertJobExecutor>.Instance);

            var runner = new GeoprocessingLocalRunner([executor]);

            // No GlassBoxCapture is supplied: the run takes the production-equivalent path.
            var result = await runner.RunAsync(
                GdalVectorConvertJobExecutor.HandledProcessId,
                ConversionInputs());

            result.Succeeded.Should().BeTrue(result.ErrorMessage);

            // (a) The default path is sanitized and surfaces NO glass box.
            result.GlassBox.Should().BeNull();

            // The only command surfaced (the structured log) keeps the scratch redacted.
            var sanitizedCommand = result.Logs
                .Single(e => e.Metadata != null && e.Metadata.ContainsKey(GdalCommandLog.CommandMetadataKey))
                .Metadata![GdalCommandLog.CommandMetadataKey];
            sanitizedCommand.Should().NotContain(scratch);
            sanitizedCommand.Should().Contain("<scratch>");
        }
        finally
        {
            GdalCli.CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task RunAsync_GlassBox_SurfacesUnsanitizedCommandFullStderrTimelineAndPreview()
    {
        var scratch = GdalCli.NewScratch(ScratchSuite);
        try
        {
            // The fake CLI writes the converted bytes AND emits stderr the prod path would
            // truncate/sanitize; glass-box must surface the full text verbatim.
            const string FullStderr = "Warning 6: driver detail line 1\nWarning 6: driver detail line 2 with /tmp scratch hint";
            var fakeRunner = new FakeGdalCommandRunner((_, args, _) =>
            {
                File.WriteAllBytes(args[^2], ConvertedBytes); // ogr2ogr: `-f CSV <output> <input>`
                return new GdalCommandResult
                {
                    ExitCode = 0,
                    StandardOutput = "ogr2ogr stdout head",
                    StandardError = FullStderr,
                };
            });

            // The CLI's glass-box mode decorates the registered runner via
            // AddGlassBoxGdalCapture; here we wire the same decorator explicitly so the
            // executor's GDAL invocations are recorded into the shared capture.
            var capture = new GlassBoxCapture();
            var gdalRunner = new GlassBoxGdalCommandRunner(fakeRunner, capture);

            var executor = new GdalVectorConvertJobExecutor(
                gdalRunner,
                GdalJobFactory.Options(scratch),
                NullLogger<GdalVectorConvertJobExecutor>.Instance);

            var runner = new GeoprocessingLocalRunner([executor]);

            var result = await runner.RunAsync(
                GdalVectorConvertJobExecutor.HandledProcessId,
                ConversionInputs(),
                capture);

            result.Succeeded.Should().BeTrue(result.ErrorMessage);

            // (b) The dev flag surfaces the glass box.
            result.GlassBox.Should().NotBeNull();
            var glassBox = result.GlassBox!;

            // Unsanitized command: the REAL scratch path appears (no <scratch> placeholder).
            glassBox.Commands.Should().ContainSingle();
            var command = glassBox.Commands[0];
            command.Tool.Should().Be("ogr2ogr");
            command.CommandLine.Should().Contain(scratch);
            command.CommandLine.Should().NotContain("<scratch>");
            // The real per-job workspace is the scratch root plus the operation id subdir.
            command.WorkingDirectory.Should().StartWith(scratch);

            // Full, untruncated stderr/stdout.
            command.StandardError.Should().Be(FullStderr);
            command.StandardOutput.Should().Be("ogr2ogr stdout head");

            // Timeline: the executor's reported phases, in order, with timing.
            glassBox.Timeline.Should().NotBeEmpty();
            glassBox.Timeline.Select(p => p.Phase).Should().Contain("Running ogr2ogr conversion");
            glassBox.Timeline.Should().BeInAscendingOrder(p => p.Elapsed);

            // Scratch dir hint points at the REAL workspace under the scratch root.
            glassBox.ScratchDirectories.Should().ContainSingle()
                .Which.Should().StartWith(scratch);

            // (c) Artifact preview renders for the GDAL (CSV) op.
            glassBox.ArtifactPreviews.Should().ContainSingle();
            var preview = glassBox.ArtifactPreviews[0];
            preview.MediaType.Should().Be("text/csv");
            preview.SizeBytes.Should().Be(ConvertedBytes.Length);
            preview.Summary.Should().Contain("POINT(1 2)");
        }
        finally
        {
            GdalCli.CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task AddGlassBoxGdalCapture_DecoratesRunnerAndRecordsRawInvocation()
    {
        var scratch = GdalCli.NewScratch(ScratchSuite);
        try
        {
            // Prove the DI seam the CLI uses: decorating the registered runner records the
            // raw invocation while returning the inner result verbatim.
            var inner = FakeGdalCommandRunner.Succeeding(ConvertedBytes, outputArgIndexFromEnd: 1);
            var capture = new GlassBoxCapture();
            var decorator = new GlassBoxGdalCommandRunner(inner, capture);

            var args = new[] { "-f", "CSV", Path.Combine(scratch, "out.csv"), Path.Combine(scratch, "in.geojson") };
            Directory.CreateDirectory(scratch);
            var result = await decorator.RunAsync("ogr2ogr", args, scratch, CancellationToken.None);

            result.ExitCode.Should().Be(0);
            inner.Invocations.Should().ContainSingle();
            capture.Commands.Should().ContainSingle();
            capture.Commands[0].WorkingDirectory.Should().Be(scratch);
            capture.Commands[0].CommandLine.Should().Contain(scratch);
        }
        finally
        {
            GdalCli.CleanupScratch(scratch);
        }
    }

    private static Dictionary<string, string> ConversionInputs() =>
        new(StringComparer.Ordinal)
        {
            ["source"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                """{"type":"FeatureCollection","features":[{"type":"Feature","geometry":{"type":"Point","coordinates":[1,2]},"properties":{"name":"a"}}]}""")),
            ["sourceFormat"] = "GeoJSON",
            ["targetFormat"] = "CSV",
        };

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
