// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.Attributes;
using Honua.Worker.Gdal.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Honua.Worker.Gdal.Tests;

/// <summary>
/// Fake-runner coverage for <see cref="GdalRasterInterpolateJobExecutor"/>: the
/// gdal_grid IDW argument projection plus the ordinary-kriging path, whose predictions
/// are solved in managed code and handed to gdal_translate for encoding (#3932).
/// </summary>
public sealed class GdalRasterInterpolateExecutorTests
{
    private const string ScratchSuite = "honua-gdal-interpolate-test";

    private static string Base64(string text) => GdalCli.Base64(text);

    [UnitTest]
    public void GdalRasterInterpolateExecutor_DeclaresNativeRuntimeProfile_AndAdvertisesBothIds()
    {
        var executor = NewExecutor(FakeGdalCommandRunner.Failing(1, "n/a"), out _);

        executor.Kind.Should().Be(ExecutionJobKind.Geoprocessing);
        executor.AcceptedRuntimeProfiles.Should().ContainSingle().Which.Should().Be(RuntimeProfiles.Native);
        GdalRasterInterpolateJobExecutor.SupportedProcessIds.Should().BeEquivalentTo(new[]
        {
            "raster.interpolate-idw",
            "raster.interpolate-kriging",
        });
    }

    [UnitTest]
    public async Task Idw_Default_RunsGdalGridInvdist_AndPublishesGeoTiff()
    {
        var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("idw-tif"));
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterInterpolateJobExecutor.IdwProcessId,
                ("points", Base64("{\"type\":\"FeatureCollection\",\"features\":[]}")),
                ("zField", "elevation"));
            var context = new RecordingJobExecutionContext(job.OperationId);

            var result = await executor.ExecuteAsync(job, context, default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            context.Artifacts.Should().ContainSingle();
            context.Artifacts[0].Should().StartWith("data:image/tiff");

            var invocation = runner.Invocations.Single();
            invocation.Tool.Should().Be("gdal_grid");
            invocation.Arguments.Should().ContainInOrder("-a", "invdist:power=2:smoothing=0:nodata=nan");
            invocation.Arguments.Should().ContainInOrder("-zfield", "elevation");
            invocation.Arguments.Should().Contain("-l").And.Contain("points");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Idw_WithTuningAndOutsize_ProjectsAllArguments()
    {
        var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("ok"));
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterInterpolateJobExecutor.IdwProcessId,
                ("points", Base64("points")),
                ("power", "3"),
                ("smoothing", "0.5"),
                ("radius", "100"),
                ("width", "256"),
                ("height", "128"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            var args = runner.Invocations.Single().Arguments;
            args.Should().ContainInOrder("-a", "invdist:power=3:smoothing=0.5:nodata=nan:radius=100");
            args.Should().ContainInOrder("-outsize", "256", "128");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Idw_WidthWithoutHeight_FailsBeforeReachingTheCli()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterInterpolateJobExecutor.IdwProcessId,
                ("points", Base64("points")),
                ("width", "256"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("width").And.Contain("height");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Idw_InvalidZField_FailsBeforeReachingTheCli()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterInterpolateJobExecutor.IdwProcessId,
                ("points", Base64("points")),
                ("zField", "elev; rm -rf /"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("zField");
            runner.Invocations.Should().BeEmpty("an invalid attribute name must never reach the CLI");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Idw_MissingPoints_FailsWithClearMessage()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(GdalRasterInterpolateJobExecutor.IdwProcessId);

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("points");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Kriging_ValidPoints_SolvesTheSurfaceAndEncodesTwoBandsWithGdalTranslateAndMerge()
    {
        var runner = SucceedingKrigingRunner(Encoding.UTF8.GetBytes("kriging-tif"));
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterInterpolateJobExecutor.KrigingProcessId,
                ("points", Base64(PointsGeoJson)),
                ("zField", "value"),
                ("width", "4"),
                ("height", "2"));
            var context = new RecordingJobExecutionContext(job.OperationId);

            var result = await executor.ExecuteAsync(job, context, default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            context.Artifacts.Should().ContainSingle();
            context.Artifacts[0].Should().StartWith("data:image/tiff");

            // The predictions AND the kriging standard errors are the worker's own;
            // GDAL still materializes each band and combines them, so the executor
            // reaches the pinned toolchain exactly three times: one gdal_translate per
            // band, then gdal_merge.py -separate to stack them.
            runner.Invocations.Should().HaveCount(3);
            runner.Invocations.Count(i => i.Tool == "gdal_translate").Should().Be(2);
            var merge = runner.Invocations.Single(i => i.Tool == "gdal_merge.py");
            merge.Arguments.Should().Contain("-separate");
            foreach (var translate in runner.Invocations.Where(i => i.Tool == "gdal_translate"))
            {
                translate.Arguments.Should().ContainInOrder("-of", "GTiff");
                translate.Arguments.Should().ContainInOrder("-a_srs", "EPSG:4326");
            }
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Kriging_ExplicitSrid_IsPassedThroughToBothBandEncoders()
    {
        var runner = SucceedingKrigingRunner(Encoding.UTF8.GetBytes("ok"));
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterInterpolateJobExecutor.KrigingProcessId,
                ("points", Base64(PointsGeoJson)),
                ("zField", "value"),
                ("srid", "3857"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            foreach (var translate in runner.Invocations.Where(i => i.Tool == "gdal_translate"))
            {
                translate.Arguments.Should().ContainInOrder("-a_srs", "EPSG:3857");
            }
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Kriging_UnknownVariogramModel_FailsBeforeReachingTheCli()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterInterpolateJobExecutor.KrigingProcessId,
                ("points", Base64(PointsGeoJson)),
                ("zField", "value"),
                ("model", "matern"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("spherical, exponential, gaussian");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    /// <summary>
    /// The sample cap and the cell cap can BOTH be satisfied by a request whose prediction
    /// cost is their product SQUARED (the per-cell standard error solves the primal system
    /// against the cached factorization, O(samples²) per cell). That work runs in managed
    /// code before the GDAL child process exists, so ToolTimeout does not bound it; the
    /// combined budget must refuse it up front.
    /// </summary>
    [Theory]
    [InlineData(1_000, ExecutionJobStatus.Failed)]
    [InlineData(200_000, ExecutionJobStatus.Succeeded)]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task Kriging_CombinedBudget_EnforcesTheBoundary(long budget, ExecutionJobStatus expected)
    {
        var runner = SucceedingKrigingRunner(Encoding.UTF8.GetBytes("ok"));
        var scratch = GdalCli.NewScratch(ScratchSuite);
        var executor = new GdalRasterInterpolateJobExecutor(
            runner,
            // 4 samples and a 100x100 grid are each well inside their own cap; the
            // squared product (4² x 10,000 = 160,000 evaluations) is not inside a
            // budget of 1,000, but is inside a budget of 200,000.
            GdalJobFactory.Options(scratch, maxKrigingSamples: 16, maxKrigingCells: 1_000_000, maxKrigingPredictionWork: budget),
            NullLogger<GdalRasterInterpolateJobExecutor>.Instance);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterInterpolateJobExecutor.KrigingProcessId,
                ("points", Base64(PointsGeoJson)),
                ("zField", "value"),
                ("width", "100"),
                ("height", "100"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(expected, result.ErrorMessage);
            if (expected == ExecutionJobStatus.Failed)
            {
                result.ErrorMessage.Should().Contain("MaxKrigingPredictionWork");
                runner.Invocations.Should().BeEmpty("the budget must be refused before any solve or CLI work");
            }
            else
            {
                runner.Invocations.Should().HaveCount(3);
            }
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [Theory]
    [InlineData(1e300, "sample value")]
    [InlineData(-1e300, "sample value")]
    [InlineData((double)float.MaxValue, "prediction")]
    [InlineData(-(double)float.MaxValue, "prediction")]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task Kriging_ExcessiveSampleOrPredictionMagnitude_FailsBeforeEncoding(double value, string error)
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var points = FormattableString.Invariant($$$"""
            {"type":"FeatureCollection","features":[
              {"type":"Feature","properties":{"value":0},"geometry":{"type":"Point","coordinates":[0,0]}},
              {"type":"Feature","properties":{"value":{{{value}}}},"geometry":{"type":"Point","coordinates":[0,1]}},
              {"type":"Feature","properties":{"value":{{{value}}}},"geometry":{"type":"Point","coordinates":[1,0]}}]}
            """);
            var job = GdalJobFactory.Job(
                GdalRasterInterpolateJobExecutor.KrigingProcessId,
                ("points", Base64(points)), ("zField", "value"),
                ("model", "gaussian"), ("range", "10"), ("sill", "1"), ("width", "2"), ("height", "2"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("magnitude").And.Contain(error);
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task WriteGridAsync_LongRowAtMagnitudeLimit_PreservesCellsAcrossBatches()
    {
        var path = Path.GetTempFileName();
        try
        {
            var values = Enumerable.Repeat(-KrigingGridInputs.MaxAbsValue, 2048).ToArray();
            await KrigingGridInputs.WriteGridAsync(path, new KrigingGrid(0, 0, 1, 1, 2048, 1), values, default);
            new FileInfo(path).Length.Should().BeLessThan(61 * values.Length + 256);
            var rows = await File.ReadAllLinesAsync(path);
            rows.Should().HaveCount(6);
            rows[5].Split(' ').Select(cell => float.Parse(cell, CultureInfo.InvariantCulture))
                .Should().Equal(values.Select(value => (float)value));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [UnitTest]
    public async Task Kriging_MoreSamplesThanTheConfiguredCap_FailsBeforeSolving()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var scratch = GdalCli.NewScratch(ScratchSuite);
        var executor = new GdalRasterInterpolateJobExecutor(
            runner,
            GdalJobFactory.Options(scratch, maxKrigingSamples: 2),
            NullLogger<GdalRasterInterpolateJobExecutor>.Instance);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterInterpolateJobExecutor.KrigingProcessId,
                ("points", Base64(PointsGeoJson)),
                ("zField", "value"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("MaxKrigingSamples");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Kriging_CoincidentSamples_FailsWithASingularSystemMessage()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            const string coincident = """
            {"type":"FeatureCollection","features":[
              {"type":"Feature","properties":{"value":1},"geometry":{"type":"Point","coordinates":[0,0]}},
              {"type":"Feature","properties":{"value":9},"geometry":{"type":"Point","coordinates":[0,0]}}]}
            """;
            var job = GdalJobFactory.Job(
                GdalRasterInterpolateJobExecutor.KrigingProcessId,
                ("points", Base64(coincident)),
                ("zField", "value"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("share a location");
            result.ErrorMessage.Should().NotContain("raise 'nugget'");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task Idw_OverCapOutputSize_FailsBeforeReachingTheCli()
    {
        // A tiny point input requesting an enormous -outsize grid must be refused before
        // gdal_grid allocates the width×height surface (#2782).
        var runner = new FakeGdalCommandRunner((_, _, _) =>
            throw new InvalidOperationException("gdal_grid must not run for an over-cap output grid"));
        var scratch = GdalCli.NewScratch(ScratchSuite);
        var executor = new GdalRasterInterpolateJobExecutor(
            runner,
            GdalJobFactory.Options(scratch, maxRasterWidth: 4096, maxRasterHeight: 4096, maxRasterPixels: 4096L * 4096L),
            NullLogger<GdalRasterInterpolateJobExecutor>.Instance);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterInterpolateJobExecutor.IdwProcessId,
                ("points", Base64("points")),
                ("width", "1000000"),
                ("height", "1000000"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("output grid").And.Contain("exceeds configured");
            runner.Invocations.Should().BeEmpty("the over-cap output grid must be refused before gdal_grid runs");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    private const string PointsGeoJson = """
        {"type":"FeatureCollection","features":[
          {"type":"Feature","properties":{"value":10},"geometry":{"type":"Point","coordinates":[0,0]}},
          {"type":"Feature","properties":{"value":20},"geometry":{"type":"Point","coordinates":[4,0]}},
          {"type":"Feature","properties":{"value":30},"geometry":{"type":"Point","coordinates":[0,4]}},
          {"type":"Feature","properties":{"value":40},"geometry":{"type":"Point","coordinates":[4,4]}}]}
        """;

    private static GdalRasterInterpolateJobExecutor NewExecutor(IGdalCommandRunner runner, out string scratch)
    {
        scratch = GdalCli.NewScratch(ScratchSuite);
        return new GdalRasterInterpolateJobExecutor(
            runner, GdalJobFactory.Options(scratch), NullLogger<GdalRasterInterpolateJobExecutor>.Instance);
    }

    /// <summary>
    /// A fake runner for the kriging two-band pipeline: <c>gdal_translate</c>'s output
    /// path is its last CLI argument, but <c>gdal_merge.py -separate -o &lt;path&gt; ...</c>
    /// names its output earlier, after <c>-o</c>. <see cref="FakeGdalCommandRunner.Succeeding"/>
    /// only knows the fixed-offset-from-the-end convention, so this locates each tool's
    /// output path the way it actually names it.
    /// </summary>
    private static FakeGdalCommandRunner SucceedingKrigingRunner(byte[] outputBytes)
        => new((tool, args, _) =>
        {
            var outputPath = tool == "gdal_merge.py"
                ? args[Array.IndexOf(args.ToArray(), "-o") + 1]
                : args[^1];
            File.WriteAllBytes(outputPath, outputBytes);
            return new GdalCommandResult { ExitCode = 0 };
        });

    private static void CleanupScratch(string scratch) => GdalCli.CleanupScratch(scratch);
}
