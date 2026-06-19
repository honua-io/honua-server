// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.TestKit.Attributes;
using Honua.Worker.Gdal.Execution;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Worker.Gdal.Tests;

/// <summary>
/// Fake-runner coverage for <see cref="GdalMultidimCoverageMetadataJobExecutor"/>
/// (Path B, ADR-0039): VSI path projection, gdalmdiminfo invocation, the raw-JSON
/// artifact contract, and input guardrails — without GDAL installed.
/// </summary>
public sealed class GdalMultidimCoverageExecutorTests
{
    private const string GdalMdimInfoJson =
        """{"type":"group","driver":"netCDF","name":"/","arrays":{"sst":{"datatype":"Float32"}}}""";

    private static GdalMultidimCoverageMetadataJobExecutor NewExecutor(
        IGdalCommandRunner runner,
        out string scratch)
    {
        scratch = Path.Combine(Path.GetTempPath(), "honua-gdal-multidim-test");
        return new GdalMultidimCoverageMetadataJobExecutor(
            runner,
            GdalJobFactory.Options(scratch),
            NullLogger<GdalMultidimCoverageMetadataJobExecutor>.Instance);
    }

    [UnitTest]
    public async Task Execute_AwsS3Source_RunsGdalMdimInfoOnVsiPath_PublishesJson()
    {
        var runner = new FakeGdalCommandRunner((_, _, _) =>
            new GdalCommandResult { ExitCode = 0, StandardOutput = GdalMdimInfoJson });
        var executor = NewExecutor(runner, out _);

        var job = GdalJobFactory.Job(
            GdalMultidimCoverageMetadataJobExecutor.HandledProcessId,
            ("provider", "AwsS3"),
            ("bucket", "honua-cubes"),
            ("objectKey", "maui/sst.nc"));
        var context = new RecordingJobExecutionContext(job.OperationId);

        var result = await executor.ExecuteAsync(job, context, default);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);

        // gdalmdiminfo (structure) runs first, then best-effort gdalinfo (extent)
        // and gdal_translate -of Zarr (convert, so pixel slices resolve).
        var mdim = runner.Invocations[0];
        mdim.Tool.Should().Be("gdalmdiminfo");
        mdim.Arguments.Should().ContainSingle().Which.Should().Be("/vsis3/honua-cubes/maui/sst.nc");
        runner.Invocations.Should().Contain(i => i.Tool == "gdalinfo");
        var convert = runner.Invocations.Should().ContainSingle(i => i.Tool == "gdal_translate").Subject;
        convert.Arguments.Should().ContainInOrder("-of", "Zarr", "/vsis3/honua-cubes/maui/sst.nc", "/vsis3/honua-cubes/maui/sst.zarr");

        context.Artifacts.Should().ContainSingle()
            .Which.Should().StartWith("data:application/json");
    }

    [UnitTest]
    public void DeriveZarrRootPath_ReplacesExtensionWithZarr()
    {
        GdalMultidimCoverageMetadataJobExecutor.DeriveZarrRootPath("maui/sst.nc").Should().Be("maui/sst.zarr");
        GdalMultidimCoverageMetadataJobExecutor.DeriveZarrRootPath("granule.nc4").Should().Be("granule.zarr");
        GdalMultidimCoverageMetadataJobExecutor.DeriveZarrRootPath("/leading/slash.h5").Should().Be("leading/slash.zarr");
        GdalMultidimCoverageMetadataJobExecutor.DeriveZarrRootPath("noext").Should().Be("noext.zarr");
    }

    [UnitTest]
    public async Task Execute_UnknownProcessId_IsRejected()
    {
        var runner = new FakeGdalCommandRunner((_, _, _) => new GdalCommandResult { ExitCode = 0 });
        var executor = NewExecutor(runner, out _);

        var job = GdalJobFactory.Job("gdal.ogr2ogr", ("provider", "AwsS3"), ("bucket", "b"), ("objectKey", "k.nc"));
        var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        runner.Invocations.Should().BeEmpty();
    }

    [UnitTest]
    public async Task Execute_MissingBucket_Fails()
    {
        var runner = new FakeGdalCommandRunner((_, _, _) => new GdalCommandResult { ExitCode = 0, StandardOutput = GdalMdimInfoJson });
        var executor = NewExecutor(runner, out _);

        var job = GdalJobFactory.Job(
            GdalMultidimCoverageMetadataJobExecutor.HandledProcessId,
            ("provider", "AwsS3"),
            ("objectKey", "k.nc"));
        var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("bucket");
        runner.Invocations.Should().BeEmpty();
    }

    [UnitTest]
    public async Task Execute_InvalidProvider_Fails()
    {
        var runner = new FakeGdalCommandRunner((_, _, _) => new GdalCommandResult { ExitCode = 0 });
        var executor = NewExecutor(runner, out _);

        var job = GdalJobFactory.Job(
            GdalMultidimCoverageMetadataJobExecutor.HandledProcessId,
            ("provider", "Dropbox"),
            ("bucket", "b"),
            ("objectKey", "k.nc"));
        var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("provider");
    }

    [UnitTest]
    public async Task Execute_ToolFailure_FailsWithSanitizedMessage()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "ERROR 4: not recognized as a supported file format");
        var executor = NewExecutor(runner, out _);

        var job = GdalJobFactory.Job(
            GdalMultidimCoverageMetadataJobExecutor.HandledProcessId,
            ("provider", "AwsS3"),
            ("bucket", "b"),
            ("objectKey", "k.nc"));
        var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("gdalmdiminfo exited with code 1");
    }

    [UnitTest]
    public void VsiPath_MapsProvidersToHandlers()
    {
        GdalVsiPath.Build(CloudStorageProvider.AwsS3, "bucket", "a/b.nc").Should().Be("/vsis3/bucket/a/b.nc");
        GdalVsiPath.Build(CloudStorageProvider.AzureBlob, "container", "/a/b.nc").Should().Be("/vsiaz/container/a/b.nc");
    }

    [UnitTest]
    public void VsiPath_UnsupportedProvider_Throws()
    {
        var act = () => GdalVsiPath.Build((CloudStorageProvider)999, "bucket", "k.nc");
        act.Should().Throw<NotSupportedException>();
    }
}
