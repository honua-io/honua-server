// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Unit coverage for the substrate-neutral single-host local process-pool batch backend (#2460).
/// Exercises real child processes (via <c>sleep</c>, never a shell) so the launch, observe, cancel,
/// pool-cap, and lost-process paths are verified without Docker.
/// </summary>
public sealed class LocalProcessPoolBatchComputeBackendTests
{
    private static string SleepPath => File.Exists("/usr/bin/sleep") ? "/usr/bin/sleep" : "/bin/sleep";

    [Fact]
    public void BackendIdentity_MatchesContract()
    {
        using var backend = CreateBackend();
        backend.BackendName.Should().Be(LocalProcessPoolBatchComputeBackend.AdapterBackendName);
        backend.BackendName.Should().Be("honua-local-process");
        backend.TargetKind.Should().Be(BatchComputeTargetKind.LocalProcess);
    }

    [Fact]
    public async Task GetCapabilities_ReturnsExpectedFlags()
    {
        using var backend = CreateBackend();

        var capabilities = await backend.GetCapabilitiesAsync();

        capabilities.SupportsCancellation.Should().BeTrue();
        capabilities.SupportsProgressPolling.Should().BeTrue();
        capabilities.SupportsRetry.Should().BeTrue();
        capabilities.SupportsLogStreaming.Should().BeFalse();
        capabilities.SupportsArtifactStaging.Should().BeFalse();
    }

    [Fact]
    public async Task StartAsync_WithoutExecutable_ReturnsFailed()
    {
        using var backend = CreateBackend();
        var job = CreateJob("job-missing-exe", new Dictionary<string, string>());

        var result = await backend.StartAsync(job);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.Message.Should().Contain(LocalProcessParameterKeys.Executable);
    }

    [Fact]
    public async Task StartThenObserve_SuccessfulProcess_ReturnsSucceeded()
    {
        using var backend = CreateBackend();
        var job = CreateJob("job-success", new Dictionary<string, string>
        {
            [LocalProcessParameterKeys.Executable] = SleepPath,
            [LocalProcessParameterKeys.ArgumentPrefix + "0"] = "0"
        });

        var start = await backend.StartAsync(job);
        start.Status.Should().Be(ExecutionJobStatus.Running);
        start.ProviderOperationId.Should().StartWith(LocalProcessPoolBatchComputeBackend.LaunchedMarkerPrefix);

        var running = job with { Status = ExecutionJobStatus.Running, ProviderOperationId = start.ProviderOperationId };
        var terminal = await WaitForTerminalAsync(backend, running);

        terminal.Status.Should().Be(ExecutionJobStatus.Succeeded);
        terminal.PercentComplete.Should().Be(100);
    }

    [Fact]
    public async Task StartThenObserve_NonZeroExit_ReturnsFailed()
    {
        using var backend = CreateBackend();
        var job = CreateJob("job-fail", new Dictionary<string, string>
        {
            [LocalProcessParameterKeys.Executable] = SleepPath,
            // An invalid interval makes sleep exit 1 with a stderr message we tail into the failure detail.
            [LocalProcessParameterKeys.ArgumentPrefix + "0"] = "not-a-number"
        });

        var start = await backend.StartAsync(job);
        var running = job with { Status = ExecutionJobStatus.Running, ProviderOperationId = start.ProviderOperationId };

        var terminal = await WaitForTerminalAsync(backend, running);

        terminal.Status.Should().Be(ExecutionJobStatus.Failed);
        terminal.Message.Should().Contain("exited with code");
    }

    [Fact]
    public async Task CancelAsync_RunningProcess_KillsAndReportsCancelled()
    {
        using var backend = CreateBackend();
        var job = CreateJob("job-cancel", new Dictionary<string, string>
        {
            [LocalProcessParameterKeys.Executable] = SleepPath,
            [LocalProcessParameterKeys.ArgumentPrefix + "0"] = "30"
        });

        var start = await backend.StartAsync(job);
        start.Status.Should().Be(ExecutionJobStatus.Running);
        var running = job with { Status = ExecutionJobStatus.Running, ProviderOperationId = start.ProviderOperationId };

        var cancel = await backend.CancelAsync(running);
        cancel.Status.Should().Be(ExecutionJobStatus.Cancelled);

        var terminal = await WaitForTerminalAsync(backend, running);
        terminal.Status.Should().Be(ExecutionJobStatus.Cancelled);
    }

    [Fact]
    public async Task StartAsync_PoolCapReached_DefersSecondJob()
    {
        using var backend = CreateBackend(maxConcurrent: 1);
        var first = CreateJob("job-a", new Dictionary<string, string>
        {
            [LocalProcessParameterKeys.Executable] = SleepPath,
            [LocalProcessParameterKeys.ArgumentPrefix + "0"] = "30"
        });
        var second = CreateJob("job-b", new Dictionary<string, string>
        {
            [LocalProcessParameterKeys.Executable] = SleepPath,
            [LocalProcessParameterKeys.ArgumentPrefix + "0"] = "0"
        });

        var firstStart = await backend.StartAsync(first);
        firstStart.Status.Should().Be(ExecutionJobStatus.Running);

        var secondStart = await backend.StartAsync(second);
        secondStart.Status.Should().Be(ExecutionJobStatus.Provisioning);
        secondStart.ProviderOperationId.Should().StartWith(LocalProcessPoolBatchComputeBackend.PendingSubmissionMarkerPrefix);

        // Free the single slot and confirm the deferred job launches on the next observe.
        await backend.CancelAsync(first with { Status = ExecutionJobStatus.Running, ProviderOperationId = firstStart.ProviderOperationId });
        await WaitForTerminalAsync(backend, first with { Status = ExecutionJobStatus.Running, ProviderOperationId = firstStart.ProviderOperationId });

        var pending = second with { Status = ExecutionJobStatus.Provisioning, ProviderOperationId = secondStart.ProviderOperationId };
        var launched = await PollAsync(async () =>
        {
            var observation = await backend.ObserveAsync(pending);
            return observation.Status != ExecutionJobStatus.Provisioning ? observation : null;
        });

        launched.Status.Should().BeOneOf(ExecutionJobStatus.Running, ExecutionJobStatus.Succeeded);
    }

    [Fact]
    public async Task ObserveAsync_UnknownLaunchedOperation_ReturnsFailedProcessLost()
    {
        using var backend = CreateBackend();
        // A launched marker with no registry entry models a process lost to a host restart.
        var job = CreateJob("job-lost", new Dictionary<string, string>
        {
            [LocalProcessParameterKeys.Executable] = SleepPath
        }) with
        {
            Status = ExecutionJobStatus.Running,
            ProviderOperationId = LocalProcessPoolBatchComputeBackend.LaunchedMarkerPrefix + "job-lost"
        };

        var observation = await backend.ObserveAsync(job);

        observation.Status.Should().Be(ExecutionJobStatus.Failed);
        observation.Message.Should().Contain("no longer tracked");
    }

    private static async Task<BatchComputeObservation> WaitForTerminalAsync(
        LocalProcessPoolBatchComputeBackend backend,
        ExecutionJobRecord job)
        => await PollAsync(async () =>
        {
            var observation = await backend.ObserveAsync(job);
            return observation.Status is ExecutionJobStatus.Succeeded
                or ExecutionJobStatus.Failed
                or ExecutionJobStatus.Cancelled
                ? observation
                : null;
        });

    private static async Task<BatchComputeObservation> PollAsync(Func<Task<BatchComputeObservation?>> probe)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(15))
        {
            var result = await probe();
            if (result is not null)
            {
                return result;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("The local process did not reach the expected state within 15 seconds.");
    }

    private static LocalProcessPoolBatchComputeBackend CreateBackend(int maxConcurrent = 2)
        => new(
            Options.Create(new LocalProcessPoolOptions { MaxConcurrentProcesses = maxConcurrent }),
            NullLogger<LocalProcessPoolBatchComputeBackend>.Instance);

    private static ExecutionJobRecord CreateJob(string operationId, IReadOnlyDictionary<string, string> parameters)
    {
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
                WorkloadName = "test-workload",
                Parameters = parameters
            }
        };
    }
}
