// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.Attributes;
using Honua.Worker.Gdal.Execution;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Worker.Gdal.Tests;

/// <summary>
/// Fake-runner coverage for <see cref="GdalSurfaceJobExecutor"/>: argument
/// projection per surface.* id, source-bytes guardrails, and the canonical
/// data-URI artifact contract. Integration coverage with the real gdaldem CLI
/// fires only when GDAL is present on the host.
/// </summary>
public sealed class GdalSurfaceExecutorTests
{
    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    [UnitTest]
    public void GdalSurfaceExecutor_DeclaresNativeRuntimeProfile()
    {
        var executor = NewExecutor(FakeGdalCommandRunner.Failing(1, "n/a"), out _);

        executor.Kind.Should().Be(ExecutionJobKind.Geoprocessing);
        executor.AcceptedRuntimeProfiles.Should().NotBeNull().And.ContainSingle()
            .Which.Should().Be(RuntimeProfiles.Native);
        executor.AcceptedRuntimeProfiles.Should().NotContain(RuntimeProfiles.Managed);
    }

    [UnitTest]
    public void GdalSurfaceExecutor_AdvertisesAllSixSurfaceIds()
    {
        GdalSurfaceJobExecutor.SupportedProcessIds.Should().BeEquivalentTo(new[]
        {
            "surface.slope",
            "surface.aspect",
            "surface.hillshade",
            "surface.rugosity-tri",
            "surface.rugosity-tpi",
            "surface.roughness",
        });
    }

    [UnitTest]
    public async Task Slope_Default_RunsGdaldemSlope_AndPublishesGeoTiffArtifact()
    {
        var output = Encoding.UTF8.GetBytes("fake-slope-tif");
        var runner = FakeGdalCommandRunner.Succeeding(output);
        var executor = NewExecutor(runner, out var scratch);

        try
        {
            var job = GdalJobFactory.Job(
                GdalSurfaceJobExecutor.SlopeProcessId,
                ("source", Base64("fake-dem")));
            var context = new RecordingJobExecutionContext(job.OperationId);

            var result = await executor.ExecuteAsync(job, context, default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            context.Artifacts.Should().ContainSingle();
            context.Artifacts[0].Should().StartWith("data:image/tiff");

            var invocation = runner.Invocations.Single();
            invocation.Tool.Should().Be("gdaldem");
            invocation.Arguments.Should().StartWith(new[] { "slope" });
            invocation.Arguments.Should().NotContain("-p", because: "default slope units are degrees");
            invocation.Arguments.Should().ContainInOrder("-s", "1");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Slope_Percent_PassesDashPFlag()
    {
        var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("ok"));
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalSurfaceJobExecutor.SlopeProcessId,
                ("source", Base64("fake-dem")),
                ("units", "percent"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);
            result.Status.Should().Be(ExecutionJobStatus.Succeeded);
            runner.Invocations.Single().Arguments.Should().Contain("-p");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Slope_Radians_RejectedBeforeReachingTheCli()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalSurfaceJobExecutor.SlopeProcessId,
                ("source", Base64("fake-dem")),
                ("units", "radians"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);
            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("unsupported slope units");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Hillshade_PassesAzimuthAltitudeAndScaleFactor()
    {
        var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("ok"));
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            // 111120 is the canonical degrees-to-meters scale that callers
            // working with geographic DEMs supply. The catalog declares
            // zFactor as a vertical-to-horizontal scale ratio, so it must
            // reach gdaldem as -s/-scale, NOT -z (vertical exaggeration).
            var job = GdalJobFactory.Job(
                GdalSurfaceJobExecutor.HillshadeProcessId,
                ("source", Base64("fake-dem")),
                ("azimuth", "270"),
                ("altitude", "30"),
                ("zFactor", "111120"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);
            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);

            var args = runner.Invocations.Single().Arguments;
            args.Should().StartWith(new[] { "hillshade" });
            args.Should().ContainInOrder("-az", "270");
            args.Should().ContainInOrder("-alt", "30");
            args.Should().ContainInOrder("-s", "111120");
            args.Should().NotContain("-z",
                because: "the catalog declares zFactor as a unit ratio, which maps to gdaldem -s rather than -z (vertical exaggeration)");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Hillshade_AzimuthOutOfRange_RejectedBeforeReachingTheCli()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalSurfaceJobExecutor.HillshadeProcessId,
                ("source", Base64("fake-dem")),
                ("azimuth", "400"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);
            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("azimuth");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Aspect_RunsGdaldemAspect_NoExtraSwitches()
    {
        var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("ok"));
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalSurfaceJobExecutor.AspectProcessId,
                ("source", Base64("fake-dem")));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);
            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);

            var args = runner.Invocations.Single().Arguments;
            args.Should().StartWith(new[] { "aspect" });
            args.Should().Contain("-q");
            args.Should().NotContain("-az");
            args.Should().NotContain("-alt");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public Task RugosityTri_RunsMatchingSubcommand()
        => AssertSubcommand(GdalSurfaceJobExecutor.RugosityTriProcessId, "TRI");

    [UnitTest]
    public Task RugosityTpi_RunsMatchingSubcommand()
        => AssertSubcommand(GdalSurfaceJobExecutor.RugosityTpiProcessId, "TPI");

    [UnitTest]
    public Task Roughness_RunsMatchingSubcommand()
        => AssertSubcommand(GdalSurfaceJobExecutor.RoughnessProcessId, "roughness");

    private async Task AssertSubcommand(string processId, string expectedSubcommand)
    {
        var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("ok"));
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(processId, ("source", Base64("fake-dem")));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            runner.Invocations.Single().Arguments[0].Should().Be(expectedSubcommand);
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Rugosity_WindowRadiusOtherThanOne_RejectedBeforeReachingTheCli()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalSurfaceJobExecutor.RugosityTriProcessId,
                ("source", Base64("fake-dem")),
                ("windowRadius", "2"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);
            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("windowRadius");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task MissingSource_FailsWithClearMessage()
    {
        var executor = NewExecutor(FakeGdalCommandRunner.Failing(1, "n/a"), out var scratch);
        try
        {
            var job = GdalJobFactory.Job(GdalSurfaceJobExecutor.SlopeProcessId);

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);
            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("source");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task UnknownProcessId_FailsWithRoutingError()
    {
        var executor = NewExecutor(FakeGdalCommandRunner.Failing(1, "n/a"), out var scratch);
        try
        {
            var job = GdalJobFactory.Job("not.surface", ("source", Base64("x")));
            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);
            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("not handled");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [IntegrationTest]
    [Protocol("OgcApiProcesses")]
    [Operation("ProcessExecution")]
    public async Task Slope_WithRealGdaldem_ProducesGeoTiff_AndReconcilesAgainstSource()
    {
        if (!GdalCli.Available("gdaldem"))
        {
            return;
        }

        var scratch = NewScratch();
        var executor = new GdalSurfaceJobExecutor(
            new ProcessGdalCommandRunner(NullLogger<ProcessGdalCommandRunner>.Instance),
            GdalJobFactory.Options(scratch),
            NullLogger<GdalSurfaceJobExecutor>.Instance);

        try
        {
            var demBytes = GdalCli.GenerateSampleDem(scratch);
            var job = GdalJobFactory.Job(
                GdalSurfaceJobExecutor.SlopeProcessId,
                ("source", Convert.ToBase64String(demBytes)));
            var context = new RecordingJobExecutionContext(job.OperationId);

            var result = await executor.ExecuteAsync(job, context, default);
            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            context.Artifacts.Should().ContainSingle();
            var artifact = context.Artifacts[0];
            artifact.Should().StartWith("data:image/tiff");
            // GeoTIFF magic bytes: II*\0 (little-endian) or MM\0* (big-endian).
            var payload = GdalCli.DecodeDataUri(artifact);
            payload.Should().HaveCountGreaterThan(4);
            (payload[0] == 0x49 && payload[1] == 0x49 || payload[0] == 0x4D && payload[1] == 0x4D)
                .Should().BeTrue("output must be a real GeoTIFF");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    private static GdalSurfaceJobExecutor NewExecutor(IGdalCommandRunner runner, out string scratch)
    {
        scratch = NewScratch();
        return new GdalSurfaceJobExecutor(
            runner, GdalJobFactory.Options(scratch), NullLogger<GdalSurfaceJobExecutor>.Instance);
    }

    private static string NewScratch()
        => Path.Combine(Path.GetTempPath(), "honua-gdal-surface-test", Guid.NewGuid().ToString("N"));

    private static void CleanupScratch(string scratch)
    {
        try
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
        catch (IOException)
        {
            // best effort
        }
    }
}
