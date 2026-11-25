// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.IO.Compression;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.TestKit.Attributes;
using Honua.Worker.Gdal.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Worker.Gdal.Tests;

public sealed class WorkerCrashIsolationAndQuarantineTests : IDisposable
{
    private readonly string _scratchRoot = Path.Join(
        Path.GetTempPath(),
        "honua-gdal-crash-isolation-" + Guid.NewGuid().ToString("N"));

    public WorkerCrashIsolationAndQuarantineTests()
    {
        Directory.CreateDirectory(_scratchRoot);
    }

    public void Dispose()
    {
        GdalCli.CleanupScratch(_scratchRoot);
    }

    [UnitTest]
    public void GdalErrorSanitizer_ChildCrashWithPathsStackTracesAndSecrets_RedactsAllSensitiveDetails()
    {
        const string workspace = "/tmp/honua-gdal-worker/job-segfault-99";
        var crashStderr = string.Join('\n',
            "Traceback (most recent call last):",
            "  File \"/app/Scripts/gdal_valid_counts.py\", line 42, in main",
            "    ds = gdal.Open('/tmp/honua-gdal-worker/job-segfault-99/input.tif')",
            "   at Honua.Worker.Gdal.Execution.Runner.Execute(String arg) in /src/Runner.cs:line 88",
            "#0 0x00007f1234567890 in GDALOpen (/usr/lib/x86_64-linux-gnu/libgdal.so.34+0x1234)",
            $"ERROR 1: {workspace}/input.tif failed reading /etc/honua/keys/master.pem",
            "AWS_SECRET_ACCESS_KEY=super-secret-aws-key PGPASSWORD=db-secret-pass Authorization: Bearer eyJsecret-jwt-token",
            "curl failed for /vsis3/private-bucket/tenant/dem.tif at http://minio.internal:9000/private-bucket");

        var sanitized = GdalErrorSanitizer.Sanitize(crashStderr, workspace, ["minio.internal"]);

        sanitized.Should().NotContain("/tmp/honua-gdal-worker");
        sanitized.Should().NotContain("/etc/honua/keys");
        sanitized.Should().NotContain("/usr/lib");
        sanitized.Should().NotContain("Traceback");
        sanitized.Should().NotContain("gdal_valid_counts.py");
        sanitized.Should().NotContain("Honua.Worker.Gdal.Execution.Runner");
        sanitized.Should().NotContain("0x00007f1234567890");
        sanitized.Should().NotContain("super-secret-aws-key");
        sanitized.Should().NotContain("db-secret-pass");
        sanitized.Should().NotContain("eyJsecret-jwt-token");
        sanitized.Should().NotContain("/vsis3/");
        sanitized.Should().NotContain("minio.internal");
        sanitized.Should().Contain("<scratch>/input.tif");
        sanitized.Length.Should().BeLessThanOrEqualTo(WorkspacePathSanitizer.DefaultMaxLength + 1);
    }

    [UnitTest]
    public async Task SurfaceExecutor_SimulatedSegfaultAndSigkill_ReturnsSanitizedFailureAndCleansScratch()
    {
        int[] crashExitCodes = [139, 137];

        foreach (var exitCode in crashExitCodes)
        {
            var job = GdalJobFactory.Job(
                GdalSurfaceJobExecutor.SlopeProcessId,
                ("source", Convert.ToBase64String(TiffHeaderBuilder.Classic(64, 64, bands: 1, bits: 8))));
            var jobWorkspace = Path.Join(_scratchRoot, job.OperationId);
            var stderr =
                $"Traceback (most recent call last):\n" +
                $"  File \"/app/Scripts/crash.py\", line 12, in run\n" +
                $"    boom()\n" +
                $"Segmentation fault at {jobWorkspace}/input.tif AWS_SECRET_ACCESS_KEY=leak-me /etc/shadow";
            var executor = CreateSurfaceExecutor(FakeGdalCommandRunner.Failing(exitCode, stderr));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), CancellationToken.None);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain($"exited with code {exitCode}");
            result.ErrorMessage.Should().NotContain(job.OperationId);
            result.ErrorMessage.Should().NotContain("/etc/shadow");
            result.ErrorMessage.Should().NotContain("Traceback");
            result.ErrorMessage.Should().NotContain("leak-me");
            Directory.Exists(jobWorkspace).Should().BeFalse("per-job scratch must be cleaned up after child crash");
        }
    }

    [UnitTest]
    public async Task ProcessRunner_RealSubprocessCrashAndSecretStripping_IsolatesFailureCleanly()
    {
        var runner = CreateProcessRunner();
        var (tool, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", (IReadOnlyList<string>)["/c", "echo ERROR 1: /etc/secret PGPASSWORD=hidden 1>&2 & exit /b 137"])
            : ("/bin/sh", ["-c", "printf 'ERROR 1: /etc/secret PGPASSWORD=hidden\\n' >&2; kill -9 $$"]);

        var result = await runner.RunAsync(tool, args, _scratchRoot, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        var sanitized = GdalErrorSanitizer.Sanitize(result.StandardError, _scratchRoot);
        sanitized.Should().NotContain("/etc/secret");
        sanitized.Should().NotContain("hidden");
    }

    [UnitTest]
    public async Task BoundedProcessOutputBuffer_PathologicalOutputFlood_StaysStrictlyBoundedWithoutOom()
    {
        const int maxChars = 4096;
        var buffer = BoundedProcessOutputBuffer.CreateWithoutTruncationMarker(maxChars);
        var payloadLine = new string('X', 1024);

        var writers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 2500; i++)
            {
                buffer.AppendLine(payloadLine);
            }
        }));
        await Task.WhenAll(writers);

        buffer.Length.Should().BeLessThanOrEqualTo(maxChars);
        buffer.ToString().Length.Should().BeLessThanOrEqualTo(maxChars);
        buffer.IsTruncated.Should().BeTrue();
        buffer.TruncatedLineCount.Should().BeGreaterThan(0);
    }

    [UnitTest]
    public async Task ProcessRunner_PathologicalStderrFlood_CompletesWithoutHangAndStaysBounded()
    {
        var runner = CreateProcessRunner();
        var (tool, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", (IReadOnlyList<string>)["/c", "for /L %i in (1,1,1500) do @echo EEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEE 1>&2 & exit /b 1"])
            : ("/bin/sh", ["-c", "awk 'BEGIN{s=\"EEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEE\"; for(i=0;i<2000;i++) print s > \"/dev/stderr\"; exit 1}'"]);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await runner.RunAsync(tool, args, _scratchRoot, cts.Token);

        result.Succeeded.Should().BeFalse();
        var maxStderrLength = BoundedProcessOutputBuffer.DefaultMaxErrorChars
            + Environment.NewLine.Length
            + BoundedProcessOutputBuffer.TruncationMarker.Length;
        result.StandardError.Length.Should().BeLessThanOrEqualTo(maxStderrLength);
        result.StandardError.Length.Should().BeGreaterThan(0);
    }

    [UnitTest]
    public async Task ProcessRunner_StdoutOverflowWithZeroExitCode_FailsInsteadOfReturningTruncatedStdout()
    {
        var runner = CreateProcessRunner();
        runner.MaxOutputChars = 512;
        var (tool, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", (IReadOnlyList<string>)["/c", "for /L %i in (1,1,60) do @echo {\"band\":1,\"value\":\"0123456789abcdef\"}"])
            : ("/bin/sh", ["-c", "awk 'BEGIN{for(i=0;i<60;i++) print \"{\\\"band\\\":1,\\\"value\\\":\\\"0123456789abcdef\\\"}\"}'"]);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await runner.RunAsync(tool, args, _scratchRoot, cts.Token);

        result.Succeeded.Should().BeFalse("truncated stdout must never be surfaced as a successful command");
        result.ExitCode.Should().NotBe(0);
        result.StandardOutput.Should().BeEmpty();
        result.StandardError.Should().Contain(BoundedProcessOutputBuffer.StdoutOverflowMessage);
    }

    [UnitTest]
    public async Task PoisonPillInputs_RejectedBeforeNativeExecution_AndSubsequentValidJobSucceeds()
    {
        var runner = FakeGdalCommandRunner.Succeeding(TiffHeaderBuilder.Classic(16, 16, bands: 1, bits: 8));
        var surfaceExecutor = CreateSurfaceExecutor(runner);

        await AssertPoisonRastersRejectedAsync(surfaceExecutor);
        runner.Invocations.Should().BeEmpty("poison-pill rasters must be rejected before GDAL executes");

        await AssertZipBombRejectedAsync();
        await AssertValidSurfaceJobSucceedsAsync(surfaceExecutor, runner);
    }

    [UnitTest]
    public async Task LocalProcessPool_CrashAndCancellation_ReleasesSlotCleansScratchAndServesNextJob()
    {
        var poolRoot = Path.Join(_scratchRoot, "pool");
        using var backend = CreateLocalProcessBackend(poolRoot);

        var crashJob = CreateShellJob("op-crash", OperatingSystem.IsWindows() ? "exit /b 137" : "kill -9 $$");
        var crashTerminal = await RunToTerminalAsync(backend, crashJob);
        crashTerminal.Status.Should().Be(ExecutionJobStatus.Failed);
        Directory.Exists(Path.Join(poolRoot, "op-crash")).Should().BeFalse();

        var hangJob = CreateShellJob("op-hang", OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1 > nul" : "sleep 30");
        var hangStart = await backend.StartAsync(hangJob, CancellationToken.None);
        hangStart.Status.Should().Be(ExecutionJobStatus.Running);
        var runningHang = hangJob with { Status = ExecutionJobStatus.Running, ProviderOperationId = hangStart.ProviderOperationId };
        await backend.CancelAsync(runningHang, CancellationToken.None);
        var cancelTerminal = await WaitForTerminalAsync(backend, runningHang);
        cancelTerminal.Status.Should().Be(ExecutionJobStatus.Cancelled);
        Directory.Exists(Path.Join(poolRoot, "op-hang")).Should().BeFalse();

        var okJob = CreateShellJob("op-ok", "exit 0");
        var okTerminal = await RunToTerminalAsync(backend, okJob);
        okTerminal.Status.Should().Be(ExecutionJobStatus.Succeeded);
        Directory.Exists(Path.Join(poolRoot, "op-ok")).Should().BeFalse();
    }

    private async Task AssertPoisonRastersRejectedAsync(GdalSurfaceJobExecutor surfaceExecutor)
    {
        byte[][] poisonRasters =
        [
            TiffHeaderBuilder.Classic(width: 1_000_000, height: 1_000_000, bands: 1, bits: 8),
            TiffHeaderBuilder.Png(width: 100_000, height: 100_000, colourType: 2),
            [0xFF, 0x4F, 0xFF, 0x51, 0x00, 0x2F, 0x00, 0x00],
            [0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04],
        ];

        foreach (var poison in poisonRasters)
        {
            var poisonJob = GdalJobFactory.Job(
                GdalSurfaceJobExecutor.SlopeProcessId,
                ("source", Convert.ToBase64String(poison)));
            var poisonResult = await surfaceExecutor.ExecuteAsync(
                poisonJob, new RecordingJobExecutionContext(poisonJob.OperationId), CancellationToken.None);

            poisonResult.Status.Should().Be(ExecutionJobStatus.Failed);
            Directory.Exists(Path.Join(_scratchRoot, poisonJob.OperationId)).Should().BeFalse();
        }
    }

    private async Task AssertZipBombRejectedAsync()
    {
        var zipBomb = CreateZipBomb(256 * 1024);
        var vectorRunner = new FakeGdalCommandRunner((_, _, _) =>
            throw new InvalidOperationException("ogr2ogr must not run for a ZIP compression-ratio bomb"));
        var vectorExecutor = new GdalVectorSourceReadJobExecutor(
            vectorRunner,
            GdalJobFactory.Options(_scratchRoot),
            NullLogger<GdalVectorSourceReadJobExecutor>.Instance);
        var zipJob = GdalJobFactory.Job(
            GdalVectorSourceReadJobExecutor.HandledProcessId,
            ("source", Convert.ToBase64String(zipBomb)),
            ("sourceFormat", "GeoJSON"));

        var zipResult = await vectorExecutor.ExecuteAsync(
            zipJob, new RecordingJobExecutionContext(zipJob.OperationId), CancellationToken.None);

        zipResult.Status.Should().Be(ExecutionJobStatus.Failed);
        zipResult.ErrorMessage.Should().Contain("compression ratio");
        vectorRunner.Invocations.Should().BeEmpty();
    }

    private async Task AssertValidSurfaceJobSucceedsAsync(
        GdalSurfaceJobExecutor surfaceExecutor,
        FakeGdalCommandRunner runner)
    {
        var validJob = GdalJobFactory.Job(
            GdalSurfaceJobExecutor.SlopeProcessId,
            ("source", Convert.ToBase64String(TiffHeaderBuilder.Classic(64, 64, bands: 1, bits: 8))));
        var validContext = new RecordingJobExecutionContext(validJob.OperationId);

        var validResult = await surfaceExecutor.ExecuteAsync(validJob, validContext, CancellationToken.None);

        validResult.Status.Should().Be(ExecutionJobStatus.Succeeded, validResult.ErrorMessage);
        runner.Invocations.Should().ContainSingle();
        validContext.Artifacts.Should().ContainSingle();
        Directory.Exists(Path.Join(_scratchRoot, validJob.OperationId)).Should().BeFalse();
    }

    private GdalSurfaceJobExecutor CreateSurfaceExecutor(FakeGdalCommandRunner runner)
        => new(runner, GdalJobFactory.Options(_scratchRoot), NullLogger<GdalSurfaceJobExecutor>.Instance);

    private static ProcessGdalCommandRunner CreateProcessRunner()
        => new(
            Options.Create(new GdalHardeningOptions()),
            Options.Create(new AwsS3Options()),
            Options.Create(new AzureBlobOptions()),
            NullLogger<ProcessGdalCommandRunner>.Instance);

    private static byte[] CreateZipBomb(int zeroCount)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("bomb.geojson", CompressionLevel.Optimal);
            using var writer = entry.Open();
            writer.Write(new byte[zeroCount]);
        }

        return stream.ToArray();
    }

    private static LocalProcessPoolBatchComputeBackend CreateLocalProcessBackend(string poolRoot)
    {
        var (shell, flag) = ResolveShell();
        return new LocalProcessPoolBatchComputeBackend(
            Options.Create(new LocalProcessPoolOptions
            {
                MaxConcurrentProcesses = 1,
                WorkingRoot = poolRoot,
                WorkerContracts =
                [
                    new LocalProcessWorkerContractOptions
                    {
                        Executable = shell,
                        ArgumentPrefix = [flag],
                        MaxSupportedContractVersion = 1,
                    },
                ],
            }),
            NullLogger<LocalProcessPoolBatchComputeBackend>.Instance);
    }

    private static (string Shell, string Flag) ResolveShell()
    {
        var shell = OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"
            : (File.Exists("/usr/bin/sh") ? "/usr/bin/sh" : "/bin/sh");
        var flag = OperatingSystem.IsWindows() ? "/c" : "-c";
        return (shell, flag);
    }

    private static ExecutionJobRecord CreateShellJob(string operationId, string script)
    {
        var (shell, flag) = ResolveShell();
        var now = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = operationId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            Spec = new ExecutionJobSpec
            {
                TargetKind = BatchComputeTargetKind.LocalProcess,
                Backend = LocalProcessPoolBatchComputeBackend.AdapterBackendName,
                Kind = ExecutionJobKind.Geoprocessing,
                WorkloadName = "isolation-test",
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [LocalProcessParameterKeys.Executable] = shell,
                    [LocalProcessParameterKeys.ArgumentPrefix + "0"] = flag,
                    [LocalProcessParameterKeys.ArgumentPrefix + "1"] = script,
                },
            },
        };
    }

    private static async Task<BatchComputeObservation> RunToTerminalAsync(
        LocalProcessPoolBatchComputeBackend backend,
        ExecutionJobRecord job)
    {
        var start = await backend.StartAsync(job, CancellationToken.None);
        start.Status.Should().BeOneOf(ExecutionJobStatus.Running, ExecutionJobStatus.Provisioning);
        var active = job with { Status = start.Status, ProviderOperationId = start.ProviderOperationId };
        return await WaitForTerminalAsync(backend, active);
    }

    private static async Task<BatchComputeObservation> WaitForTerminalAsync(
        LocalProcessPoolBatchComputeBackend backend,
        ExecutionJobRecord runningJob)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(15))
        {
            var observation = await backend.ObserveAsync(runningJob, CancellationToken.None);
            if (observation.Status.IsTerminal())
            {
                return observation;
            }

            await Task.Delay(50, CancellationToken.None);
        }

        throw new TimeoutException($"Local process '{runningJob.OperationId}' did not reach a terminal state within 15 seconds.");
    }
}
