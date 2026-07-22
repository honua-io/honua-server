// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Honua.CloudIntegration.Tests;

/// <summary>
/// ADR-0060 substrate-neutral GEOPROCESSING lane (#2166, #2457). Proves the single-host, zero-cloud GP
/// executor by driving the REAL <see cref="LocalProcessPoolBatchComputeBackend"/> against real child
/// processes: run-to-completion with exit-code mapping, cancellation that kills the process tree,
/// non-blocking pool-saturation admission (pending marker then launch), the injected <c>HONUA_*</c>
/// environment contract, and the serving↔worker job-contract-version gate.
///
/// The child binary is a shell-free managed helper compiled by
/// <see cref="LocalSubstrateProcessHelperFixture"/> and launched via the <c>dotnet</c> muxer, so no OS
/// coreutil is required. Tests that launch a process skip when that helper is unavailable.
/// </summary>
[Trait(CloudIntegrationTraits.Category, CloudIntegrationTraits.LocalSubstrate)]
public sealed class LocalSubstrateProcessPoolTests : IClassFixture<LocalSubstrateProcessHelperFixture>
{
    private readonly LocalSubstrateProcessHelperFixture _helper;

    public LocalSubstrateProcessPoolTests(LocalSubstrateProcessHelperFixture helper)
    {
        _helper = helper;
    }

    [SkippableFact]
    public async Task ProcessJob_RunsToCompletion_ReportsSucceeded()
    {
        Skip.IfNot(_helper.Available, "The GP process helper could not be compiled/launched.");

        using var context = CreateBackend(maxConcurrent: 2);
        var job = CreateJob(context, "success", "--exit", "0");

        var submission = await context.Backend.StartAsync(job);
        submission.Status.Should().Be(ExecutionJobStatus.Running);
        submission.ProviderOperationId.Should().StartWith(LocalProcessPoolBatchComputeBackend.LaunchedMarkerPrefix);

        var terminal = await PollUntilTerminalAsync(context.Backend, job with { ProviderOperationId = submission.ProviderOperationId });
        terminal.Status.Should().Be(ExecutionJobStatus.Succeeded, "a child process that exits 0 maps to Succeeded");
    }

    [SkippableFact]
    public async Task ProcessJob_NonZeroExit_ReportsFailed()
    {
        Skip.IfNot(_helper.Available, "The GP process helper could not be compiled/launched.");

        using var context = CreateBackend(maxConcurrent: 2);
        var job = CreateJob(context, "failure", "--exit", "7");

        var submission = await context.Backend.StartAsync(job);
        submission.Status.Should().Be(ExecutionJobStatus.Running);

        var terminal = await PollUntilTerminalAsync(context.Backend, job with { ProviderOperationId = submission.ProviderOperationId });
        terminal.Status.Should().Be(ExecutionJobStatus.Failed, "a child process that exits non-zero maps to Failed, not Succeeded");
        terminal.Message.Should().Contain("7", "the exit code is surfaced in the failure message");
    }

    [SkippableFact]
    public async Task ProcessJob_CancelWhileRunning_ReportsCancelled()
    {
        Skip.IfNot(_helper.Available, "The GP process helper could not be compiled/launched.");

        using var context = CreateBackend(maxConcurrent: 2);
        var job = CreateJob(context, "cancel", "--sleep", "60");

        var submission = await context.Backend.StartAsync(job);
        submission.Status.Should().Be(ExecutionJobStatus.Running);
        var running = job with { ProviderOperationId = submission.ProviderOperationId };

        var cancel = await context.Backend.CancelAsync(running);
        cancel.Status.Should().Be(ExecutionJobStatus.Cancelled, "cancelling a still-running child process kills the tree and reports Cancelled");

        // The monitor settles the terminal state to Cancelled once the killed process is reaped.
        var terminal = await PollUntilTerminalAsync(context.Backend, running);
        terminal.Status.Should().Be(ExecutionJobStatus.Cancelled);
    }

    [SkippableFact]
    public async Task ProcessPool_WhenSaturated_DefersWithPendingMarkerThenLaunches()
    {
        Skip.IfNot(_helper.Available, "The GP process helper could not be compiled/launched.");

        // Pool size 1: the second submission cannot get a slot and must defer non-blockingly.
        using var context = CreateBackend(maxConcurrent: 1);

        var occupying = CreateJob(context, "occupy", "--sleep", "4");
        var deferred = CreateJob(context, "deferred", "--exit", "0");

        var occupyingSubmission = await context.Backend.StartAsync(occupying);
        occupyingSubmission.Status.Should().Be(ExecutionJobStatus.Running, "the first job takes the single pool slot");

        // Saturated: admission defers with a pending marker rather than blocking on the reconcile lease.
        var deferredSubmission = await context.Backend.StartAsync(deferred);
        deferredSubmission.Status.Should().Be(ExecutionJobStatus.Provisioning);
        deferredSubmission.ProviderOperationId.Should().StartWith(LocalProcessPoolBatchComputeBackend.PendingSubmissionMarkerPrefix);

        var pending = deferred with { ProviderOperationId = deferredSubmission.ProviderOperationId };

        // While the slot is still held, observing the pending job keeps deferring.
        var stillPending = await context.Backend.ObserveAsync(pending);
        stillPending.Status.Should().Be(ExecutionJobStatus.Provisioning, "the pool is still saturated");

        // Once the occupying job frees the slot, a later observe launches and completes the deferred job.
        var terminal = await PollUntilTerminalAsync(context.Backend, pending, TimeSpan.FromSeconds(30));
        terminal.Status.Should().Be(ExecutionJobStatus.Succeeded, "the deferred job launches and completes once a slot frees");
    }

    [SkippableFact]
    public async Task ProcessJob_InjectsHonuaEnvironmentContract()
    {
        Skip.IfNot(_helper.Available, "The GP process helper could not be compiled/launched.");

        using var context = CreateBackend(maxConcurrent: 2);
        // The second segment is a generated relative literal, so it can never be rooted and drop
        // WorkingRoot.
        var envFile = Path.Join(context.WorkingRoot, $"env-{Guid.NewGuid():N}.txt");

        var job = CreateJob(
            context,
            "env-contract",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["env.CUSTOM_FLAG"] = "honua-ci-local-substrate"
            },
            "--env-out",
            envFile);

        var submission = await context.Backend.StartAsync(job);
        submission.Status.Should().Be(ExecutionJobStatus.Running);

        var terminal = await PollUntilTerminalAsync(context.Backend, job with { ProviderOperationId = submission.ProviderOperationId });
        terminal.Status.Should().Be(ExecutionJobStatus.Succeeded);

        File.Exists(envFile).Should().BeTrue("the child process writes the injected environment to the file");
        var lines = await File.ReadAllLinesAsync(envFile);

        // The HONUA_* contract mirrors what the cloud batch backends inject, so a workload behaves
        // identically whether it runs here as a child process or on a cloud batch service.
        lines.Should().Contain($"HONUA_OPERATION_ID={job.OperationId}");
        lines.Should().Contain($"HONUA_WORKLOAD_NAME={job.Spec.WorkloadName}");
        lines.Should().Contain("HONUA_JOB_KIND=Geoprocessing");
        lines.Should().Contain($"HONUA_WORKLOAD_ID={job.Spec.WorkloadId}");
        lines.Should().Contain("CUSTOM_FLAG=honua-ci-local-substrate");
    }

    [Fact]
    public async Task ProcessPool_DeclaresBaselineContractVersion_SoNewerContractJobsAreRejected()
    {
        // The serving↔worker job-contract gate (ADR-0060 principle #3b) is enforced by the shared
        // dispatcher, which consults the backend's MaxSupportedContractVersion. This asserts the local
        // process backend declares the baseline (v1) so a v2 job is refused during a rolling version
        // step — the backend seam the dispatcher's rejection at
        // GeoprocessingJobDispatcher (contract-version gate) depends on.
        using var context = CreateBackend(maxConcurrent: 1);

        var capabilities = await context.Backend.GetCapabilitiesAsync();
        capabilities.MaxSupportedContractVersion.Should().Be(1);

        // A v1 job runs on this backend; a v2 job exceeds its supported contract and is rejected by the gate.
        (1 > capabilities.MaxSupportedContractVersion).Should().BeFalse();
        (2 > capabilities.MaxSupportedContractVersion).Should().BeTrue();
    }

    private BackendContext CreateBackend(int maxConcurrent)
    {
        var workingRoot = Directory.CreateTempSubdirectory("honua-ls-gp-run").FullName;
        var backend = new LocalProcessPoolBatchComputeBackend(
            Options.Create(new LocalProcessPoolOptions
            {
                MaxConcurrentProcesses = maxConcurrent,
                WorkingRoot = workingRoot
            }),
            NullLogger<LocalProcessPoolBatchComputeBackend>.Instance);

        return new BackendContext(backend, workingRoot);
    }

    private ExecutionJobRecord CreateJob(
        BackendContext context,
        string label,
        params string[] helperArgs)
        => CreateJob(context, label, extraParameters: null, helperArgs);

    private ExecutionJobRecord CreateJob(
        BackendContext context,
        string label,
        IReadOnlyDictionary<string, string>? extraParameters,
        params string[] helperArgs)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LocalProcessParameterKeys.Executable] = _helper.DotnetMuxerPath,
            // arg.0 is the helper assembly; the behaviour flags follow in order.
            [LocalProcessParameterKeys.ArgumentPrefix + "0"] = _helper.HelperDllPath
        };
        for (var i = 0; i < helperArgs.Length; i++)
        {
            parameters[LocalProcessParameterKeys.ArgumentPrefix + (i + 1).ToString(CultureInfo.InvariantCulture)] = helperArgs[i];
        }

        if (extraParameters is not null)
        {
            foreach (var (key, value) in extraParameters)
            {
                parameters[key] = value;
            }
        }

        var now = DateTimeOffset.UtcNow;
        var spec = new ExecutionJobSpec
        {
            Kind = ExecutionJobKind.Geoprocessing,
            TargetKind = BatchComputeTargetKind.LocalProcess,
            Backend = LocalProcessPoolBatchComputeBackend.AdapterBackendName,
            WorkloadId = $"ls-gp-{label}",
            WorkloadName = $"local-substrate-{label}",
            Parameters = parameters
        };

        return new ExecutionJobRecord
        {
            OperationId = $"ls-gp-{label}-{Guid.NewGuid():N}"[..24],
            Status = ExecutionJobStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            Spec = spec
        };
    }

    private static async Task<BatchComputeObservation> PollUntilTerminalAsync(
        LocalProcessPoolBatchComputeBackend backend,
        ExecutionJobRecord job,
        TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(20));
        BatchComputeObservation last = new() { Status = ExecutionJobStatus.Running };
        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await backend.ObserveAsync(job);
            if (last.Status is ExecutionJobStatus.Succeeded
                or ExecutionJobStatus.Failed
                or ExecutionJobStatus.Cancelled)
            {
                return last;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }

        throw new TimeoutException($"Local process job did not reach a terminal state. Last status: {last.Status}.");
    }

    private sealed class BackendContext(LocalProcessPoolBatchComputeBackend backend, string workingRoot) : IDisposable
    {
        public LocalProcessPoolBatchComputeBackend Backend { get; } = backend;

        public string WorkingRoot { get; } = workingRoot;

        public void Dispose()
        {
            Backend.Dispose();
            try
            {
                Directory.Delete(WorkingRoot, recursive: true);
            }
            catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
            {
                // Best-effort temp cleanup.
            }
        }
    }
}
