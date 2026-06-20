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
/// Fake-runner coverage for <see cref="PdalPointCloudConvertJobExecutor"/>
/// (LAZ/COPC decompress + projected-CRS reprojection, #1854). Pins routing,
/// <c>pdal translate</c> argument projection, the geographic-source pass-through,
/// the CRS-token guard, the artifact size guard, and the canonical
/// uncompressed-LAS artifact contract — all without PDAL installed.
/// </summary>
public sealed class PdalPointCloudConvertExecutorTests
{
    private const string ScratchSuite = "honua-pdal-pcloud-test";

    private static string Base64(string text) => GdalCli.Base64(text);

    /// <summary>
    /// A fake runner that succeeds and writes <paramref name="outputBytes"/> to the
    /// <c>output.las</c> argument. The PDAL argument vector ends with a writer
    /// option (not the output path), so the generic last-arg
    /// <see cref="FakeGdalCommandRunner.Succeeding"/> cannot be used here — the
    /// output path is the third positional argument of <c>pdal translate</c>.
    /// </summary>
    private static FakeGdalCommandRunner SucceedingWritingOutput(byte[] outputBytes)
        => new((_, args, _) =>
        {
            var outputPath = args.First(a => a.EndsWith("output.las", StringComparison.Ordinal));
            File.WriteAllBytes(outputPath, outputBytes);
            return new GdalCommandResult { ExitCode = 0 };
        });

    // -------------------------------------------------------------------------
    // Decompression (no reprojection)
    // -------------------------------------------------------------------------

    [UnitTest]
    public async Task Translate_LazWithoutSourceCrs_DecompressesToUncompressedLas_NoReprojection()
    {
        var output = Encoding.UTF8.GetBytes("fake-uncompressed-las");
        var runner = SucceedingWritingOutput(output);
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                PdalPointCloudConvertJobExecutor.HandledProcessId,
                ("source", Base64("fake-laz-bytes")));
            var context = new RecordingJobExecutionContext(job.OperationId);

            var result = await executor.ExecuteAsync(job, context, default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            context.Artifacts.Should().ContainSingle();
            context.Artifacts[0].Should().StartWith("data:application/vnd.las");

            var invocation = runner.Invocations.Single();
            invocation.Tool.Should().Be("pdal");
            invocation.Arguments[0].Should().Be("translate");
            // No reprojection stage requested for a source without a CRS hint.
            invocation.Arguments.Should().NotContain("reprojection");
            invocation.Arguments.Should().Contain("--writers.las.compression=false");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Translate_GeographicSourceCrs_SkipsReprojectionStage()
    {
        // EPSG:4326 / 4979 are pass-through CRS the managed tiler already accepts;
        // requesting a no-op reproject would be wasteful, so the executor omits it.
        var runner = SucceedingWritingOutput(Encoding.UTF8.GetBytes("las"));
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                PdalPointCloudConvertJobExecutor.HandledProcessId,
                ("source", Base64("fake-laz-bytes")),
                ("sourceSrs", "EPSG:4979"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            runner.Invocations.Single().Arguments.Should().NotContain("reprojection");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    // -------------------------------------------------------------------------
    // Reprojection (projected source CRS)
    // -------------------------------------------------------------------------

    [UnitTest]
    public async Task Translate_ProjectedSourceCrs_AppliesReprojectionTo4979()
    {
        var runner = SucceedingWritingOutput(Encoding.UTF8.GetBytes("las"));
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            // EPSG:32610 (UTM zone 10N) — a projected CRS that must be reprojected
            // to geographic EPSG:4979 before the geographic tiler runs.
            var job = GdalJobFactory.Job(
                PdalPointCloudConvertJobExecutor.HandledProcessId,
                ("source", Base64("fake-laz-bytes")),
                ("sourceSrs", "EPSG:32610"));
            var context = new RecordingJobExecutionContext(job.OperationId);

            var result = await executor.ExecuteAsync(job, context, default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            var args = runner.Invocations.Single().Arguments;
            args.Should().Contain("reprojection");
            args.Should().Contain("--filters.reprojection.in_srs=EPSG:32610");
            args.Should().Contain("--filters.reprojection.out_srs=EPSG:4979");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Translate_BareIntegerSourceCrs_NormalizesToEpsgToken()
    {
        var runner = SucceedingWritingOutput(Encoding.UTF8.GetBytes("las"));
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                PdalPointCloudConvertJobExecutor.HandledProcessId,
                ("source", Base64("fake-laz-bytes")),
                ("sourceSrs", "26910"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            runner.Invocations.Single().Arguments.Should().Contain("--filters.reprojection.in_srs=EPSG:26910");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    // -------------------------------------------------------------------------
    // Guards
    // -------------------------------------------------------------------------

    [UnitTest]
    public async Task Translate_RejectsMalformedSourceCrsToken_BeforeReachingTheCli()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                PdalPointCloudConvertJobExecutor.HandledProcessId,
                ("source", Base64("fake-laz-bytes")),
                ("sourceSrs", "EPSG:32610; rm -rf /"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("sourceSrs");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Translate_MissingSource_Fails()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(PdalPointCloudConvertJobExecutor.HandledProcessId);

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("source");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Translate_OversizedSource_RejectedAgainstMaxArtifactBytes()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var oversized = new byte[60L * 1024L * 1024L];
            var job = GdalJobFactory.Job(
                PdalPointCloudConvertJobExecutor.HandledProcessId,
                ("source", Convert.ToBase64String(oversized)));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("MaxArtifactBytes");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Translate_ToolFailure_SurfacesSanitizedError()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "PDAL: readers.las: failed to open /scratch/input.laz");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                PdalPointCloudConvertJobExecutor.HandledProcessId,
                ("source", Base64("not-really-laz")));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("pdal translate exited with code 1");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Translate_WrongProcessId_Refused()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                "gdal.gdalwarp",
                ("source", Base64("fake-laz-bytes")));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("not handled");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    // -------------------------------------------------------------------------
    // Argument builder unit coverage (no runner)
    // -------------------------------------------------------------------------

    [UnitTest]
    public void BuildTranslateArguments_WithoutSourceSrs_OmitsReprojectionStage()
    {
        var args = PdalPointCloudConvertJobExecutor.BuildTranslateArguments("/in.laz", "/out.las", sourceSrs: null);

        args.Should().StartWith(new[] { "translate", "/in.laz", "/out.las" });
        args.Should().NotContain("reprojection");
        args.Should().Contain("--writers.las.compression=false");
    }

    [UnitTest]
    public void BuildTranslateArguments_WithSourceSrs_InsertsReprojectionTo4979()
    {
        var args = PdalPointCloudConvertJobExecutor.BuildTranslateArguments("/in.laz", "/out.las", "EPSG:32610");

        args.Should().Contain("reprojection");
        args.Should().Contain("--filters.reprojection.in_srs=EPSG:32610");
        args.Should().Contain("--filters.reprojection.out_srs=EPSG:4979");
    }

    [UnitTest]
    public void IsGeographicSrs_RecognizesPassThroughCrs()
    {
        PdalPointCloudConvertJobExecutor.IsGeographicSrs("EPSG:4326").Should().BeTrue();
        PdalPointCloudConvertJobExecutor.IsGeographicSrs("4979").Should().BeTrue();
        PdalPointCloudConvertJobExecutor.IsGeographicSrs("ogc:crs84").Should().BeTrue();
        PdalPointCloudConvertJobExecutor.IsGeographicSrs("EPSG:32610").Should().BeFalse();
    }

    [UnitTest]
    public void IsValidSrsToken_AcceptsEpsgAndRejectsInjection()
    {
        PdalPointCloudConvertJobExecutor.IsValidSrsToken("EPSG:32610").Should().BeTrue();
        PdalPointCloudConvertJobExecutor.IsValidSrsToken("26910").Should().BeTrue();
        PdalPointCloudConvertJobExecutor.IsValidSrsToken("EPSG:4326; rm -rf /").Should().BeFalse();
        PdalPointCloudConvertJobExecutor.IsValidSrsToken("").Should().BeFalse();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static PdalPointCloudConvertJobExecutor NewExecutor(IGdalCommandRunner runner, out string scratch)
    {
        scratch = GdalCli.NewScratch(ScratchSuite);
        return new PdalPointCloudConvertJobExecutor(
            runner, GdalJobFactory.Options(scratch), NullLogger<PdalPointCloudConvertJobExecutor>.Instance);
    }

    private static void CleanupScratch(string scratch) => GdalCli.CleanupScratch(scratch);
}
