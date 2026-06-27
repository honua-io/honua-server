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
/// Fake-runner coverage for <see cref="GdalRasterMapAlgebraJobExecutor"/>: gdal_calc.py
/// band-variable argument projection, the expression allow-list guard, and the
/// canonical data-URI artifact contract (#2239).
/// </summary>
public sealed class GdalRasterMapAlgebraExecutorTests
{
    private const string ScratchSuite = "honua-gdal-mapalgebra-test";

    private static string Base64(string text) => GdalCli.Base64(text);

    private static string Sources(params string[] entries)
        => string.Join(GdalRasterMapAlgebraJobExecutor.SourceSeparator, entries.Select(Base64));

    [UnitTest]
    public void GdalRasterMapAlgebraExecutor_DeclaresNativeRuntimeProfile()
    {
        var executor = NewExecutor(FakeGdalCommandRunner.Failing(1, "n/a"), out _);

        executor.Kind.Should().Be(ExecutionJobKind.Geoprocessing);
        executor.AcceptedRuntimeProfiles.Should().ContainSingle().Which.Should().Be(RuntimeProfiles.Native);
        executor.ProcessIds.Should().ContainSingle().Which.Should().Be("raster.map-algebra");
    }

    [UnitTest]
    public async Task MapAlgebra_TwoSources_ProjectsBandVariables_AndPublishesGeoTiff()
    {
        var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("calc-tif"));
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterMapAlgebraJobExecutor.HandledProcessId,
                ("sources", Sources("raster-a", "raster-b")),
                ("expression", "(A-B)/(A+B)"));
            var context = new RecordingJobExecutionContext(job.OperationId);

            var result = await executor.ExecuteAsync(job, context, default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            context.Artifacts.Should().ContainSingle();
            context.Artifacts[0].Should().StartWith("data:image/tiff");

            var invocation = runner.Invocations.Single();
            invocation.Tool.Should().Be("gdal_calc.py");
            invocation.Arguments.Should().Contain(a => a.StartsWith("-A"));
            invocation.Arguments.Should().Contain(a => a.StartsWith("-B"));
            invocation.Arguments.Should().Contain("--calc=(A-B)/(A+B)");
            invocation.Arguments.Should().Contain("--overwrite");
            invocation.Arguments[^1].Should().EndWith("output.tif");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task MapAlgebra_LeadingUnaryMinus_PassesCalcAsSingleToken()
    {
        var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("calc-tif"));
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterMapAlgebraJobExecutor.HandledProcessId,
                ("sources", Sources("raster-a")),
                ("expression", "-A"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);

            var args = runner.Invocations.Single().Arguments;
            // The expression is a single "--calc=-A" token so argparse cannot mistake
            // the leading minus for a separate option. (The band-variable flag "-A" is
            // a separate, expected argument.)
            args.Should().Contain("--calc=-A");
            args.Should().NotContain("--calc");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task MapAlgebra_DataType_PassesTypeFlag()
    {
        var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("ok"));
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterMapAlgebraJobExecutor.HandledProcessId,
                ("sources", Sources("raster-a")),
                ("expression", "A*2"),
                ("dataType", "float32"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            runner.Invocations.Single().Arguments.Should().ContainInOrder("--type", "Float32");
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task MapAlgebra_DisallowedExpression_FailsBeforeReachingTheCli()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterMapAlgebraJobExecutor.HandledProcessId,
                ("sources", Sources("raster-a")),
                ("expression", "__import__('os').system('id')"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("expression");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task MapAlgebra_ExpressionReferencesUndefinedBand_Fails()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterMapAlgebraJobExecutor.HandledProcessId,
                ("sources", Sources("raster-a")),
                ("expression", "A+B"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("B");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task MapAlgebra_MissingExpression_Fails()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var executor = NewExecutor(runner, out var scratch);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterMapAlgebraJobExecutor.HandledProcessId,
                ("sources", Sources("raster-a")));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("expression");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task MapAlgebra_AggregateDecodedBytesExceedCeiling_FailsBeforeReachingTheCli()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "n/a");
        var scratch = GdalCli.NewScratch(ScratchSuite);
        // Each source decodes to 4 bytes (under the 6-byte ceiling), but the aggregate
        // exceeds it, so GdalCalcInputs must reject before the CLI is reached.
        var executor = new GdalRasterMapAlgebraJobExecutor(
            runner, GdalJobFactory.Options(scratch, maxArtifactBytes: 6), NullLogger<GdalRasterMapAlgebraJobExecutor>.Instance);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterMapAlgebraJobExecutor.HandledProcessId,
                ("sources", Sources("ABCD", "EFGH")),
                ("expression", "A+B"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("total");
            runner.Invocations.Should().BeEmpty();
        }
        finally
        {
            CleanupScratch(scratch);
        }
    }

    private static GdalRasterMapAlgebraJobExecutor NewExecutor(IGdalCommandRunner runner, out string scratch)
    {
        scratch = GdalCli.NewScratch(ScratchSuite);
        return new GdalRasterMapAlgebraJobExecutor(
            runner, GdalJobFactory.Options(scratch), NullLogger<GdalRasterMapAlgebraJobExecutor>.Instance);
    }

    private static void CleanupScratch(string scratch) => GdalCli.CleanupScratch(scratch);
}
