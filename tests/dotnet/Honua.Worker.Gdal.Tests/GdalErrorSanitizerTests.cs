// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.Worker.Gdal.Execution;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Worker.Gdal.Tests;

/// <summary>
/// Unit coverage for <see cref="GdalErrorSanitizer"/> and end-to-end coverage
/// that every native executor's client-visible failure message routes through
/// the sanitizer so scratch workspace paths never leak onto job.ErrorMessage.
/// </summary>
public sealed class GdalErrorSanitizerTests
{
    [UnitTest]
    public void Sanitize_ReplacesWorkspacePath_WithStablePlaceholder()
    {
        var workspace = "/tmp/honua-gdal-worker/job-abc-123";
        var stderr = $"ERROR 1: {workspace}/input.tif: No such file or directory";

        var sanitized = GdalErrorSanitizer.Sanitize(stderr, workspace);

        sanitized.Should().NotContain("/tmp/honua-gdal-worker");
        sanitized.Should().NotContain("job-abc-123");
        sanitized.Should().Contain("<scratch>/input.tif");
        sanitized.Should().Contain("No such file or directory");
    }

    [UnitTest]
    public void Sanitize_HandlesEmptyAndBlankStderr_WithoutThrowing()
    {
        GdalErrorSanitizer.Sanitize("", "/scratch").Should().BeEmpty();
        GdalErrorSanitizer.Sanitize("   \n", "/scratch").Should().BeEmpty();
    }

    [UnitTest]
    public void Sanitize_PassesThrough_WhenWorkspaceIsAbsent()
    {
        const string stderr = "ERROR 1: bad input";
        GdalErrorSanitizer.Sanitize(stderr, "").Should().Be("ERROR 1: bad input");
    }

    [UnitTest]
    public void Sanitize_TruncatesVeryLongStderr_WithEllipsis()
    {
        var stderr = new string('x', 1000);
        var sanitized = GdalErrorSanitizer.Sanitize(stderr, "/scratch");
        sanitized.Length.Should().BeLessThan(stderr.Length);
        sanitized.Should().EndWith("…");
    }

    [UnitTest]
    public async Task SurfaceExecutor_FailureMessage_DoesNotLeakWorkspacePath()
    {
        var runner = FakeGdalCommandRunner.Failing(
            exitCode: 1,
            stderr: "ERROR 1: /tmp/honua-gdal-worker/leaky-op/input.tif unreadable; aborting.");
        var scratch = Path.Combine(Path.GetTempPath(), "honua-gdal-worker");
        var executor = new GdalSurfaceJobExecutor(
            runner,
            GdalJobFactory.Options(scratch),
            NullLogger<GdalSurfaceJobExecutor>.Instance);
        try
        {
            var job = GdalJobFactory.Job(
                GdalSurfaceJobExecutor.SlopeProcessId,
                ("source", Convert.ToBase64String(Encoding.UTF8.GetBytes("dem"))));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(Core.Features.ControlPlane.Domain.ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().NotBeNull();
            result.ErrorMessage!.Should().NotContain(job.OperationId,
                because: "the per-job workspace contains the operation id and must be redacted from the client-visible message");
            result.ErrorMessage.Should().Contain("gdaldem exited with code 1");
        }
        finally
        {
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); } catch (IOException) { }
        }
    }
}
