// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Raster;
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

    private static string ShPath => File.Exists("/usr/bin/sh") ? "/usr/bin/sh" : "/bin/sh";

    private static string CommandShellPath => OperatingSystem.IsWindows()
        ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"
        : ShPath;

    [Fact]
    public async Task BuildEnvironment_WorkloadCannotOverrideContractVersionGate()
    {
        using var backend = CreateBackend();
        var outFile = Path.Join(Path.GetTempPath(), $"honua-contract-{Guid.NewGuid():N}.txt");
        try
        {
            var job = CreateJob("job-gate", new Dictionary<string, string>
            {
                [LocalProcessParameterKeys.Executable] = ShPath,
                [LocalProcessParameterKeys.ArgumentPrefix + "0"] = "-c",
                [LocalProcessParameterKeys.ArgumentPrefix + "1"] = "printf '%s' \"$HONUA_CONTRACT_VERSION\" > \"$HONUA_TEST_OUT\"",
                [LocalProcessParameterKeys.EnvironmentPrefix + "HONUA_TEST_OUT"] = outFile,
                // A malicious/erroneous workload tries to weaken the serving↔worker contract gate.
                [LocalProcessParameterKeys.EnvironmentPrefix + "HONUA_CONTRACT_VERSION"] = "999"
            });

            var start = await backend.StartAsync(job);
            start.Status.Should().Be(ExecutionJobStatus.Running);

            var running = job with { Status = ExecutionJobStatus.Running, ProviderOperationId = start.ProviderOperationId };
            (await WaitForTerminalAsync(backend, running)).Status.Should().Be(ExecutionJobStatus.Succeeded);

            (await File.ReadAllTextAsync(outFile)).Should().Be("1",
                "the stamped contract-version gate must win over a workload env.HONUA_CONTRACT_VERSION passthrough.");
        }
        finally
        {
            if (File.Exists(outFile))
            {
                File.Delete(outFile);
            }
        }
    }

    [Fact]
    public async Task StartAsync_UnattestedSelectedExecutableRejectsVersionTwoBeforeLaunch()
    {
        using var backend = CreateBackend(executableContracts: new Dictionary<string, int>());
        var job = CreateJob(
            "job-unattested-v2",
            new Dictionary<string, string>
            {
                [LocalProcessParameterKeys.Executable] = ShPath,
                [LocalProcessParameterKeys.ArgumentPrefix + "0"] = "-c",
                [LocalProcessParameterKeys.ArgumentPrefix + "1"] = "exit 0"
            },
            contractVersion: 2);

        var result = await backend.StartAsync(job);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.Message.Should().Contain(ShPath).And.Contain("version 1").And.Contain("requires version 2");
    }

    [Fact]
    public async Task StartAsync_AttestedSelectedExecutableAcceptsVersionTwo()
    {
        using var backend = CreateBackend(workerContracts:
        [
            new LocalProcessWorkerContractOptions
            {
                Executable = CommandShellPath,
                ArgumentPrefix = [OperatingSystem.IsWindows() ? "/c" : "-c", "exit 0"],
                MaxSupportedContractVersion = RasterSourceContract.JobContractVersion
            }
        ]);
        var job = CreateJob(
            "job-attested-v2",
            new Dictionary<string, string>
            {
                [LocalProcessParameterKeys.Executable] = CommandShellPath,
                [LocalProcessParameterKeys.ArgumentPrefix + "0"] = OperatingSystem.IsWindows() ? "/c" : "-c",
                [LocalProcessParameterKeys.ArgumentPrefix + "1"] = "exit 0"
            },
            contractVersion: 2);

        var result = await backend.StartAsync(job);

        result.Status.Should().Be(ExecutionJobStatus.Running);
    }

    [Fact]
    public async Task StartAsync_SharedLauncherAttestationRejectsDifferentWorkerArguments()
    {
        using var backend = CreateBackend(workerContracts:
        [
            new LocalProcessWorkerContractOptions
            {
                Executable = CommandShellPath,
                ArgumentPrefix = [OperatingSystem.IsWindows() ? "/c" : "-c", "exit 0"],
                MaxSupportedContractVersion = RasterSourceContract.JobContractVersion
            }
        ]);
        var job = CreateJob(
            "job-different-worker-v2",
            new Dictionary<string, string>
            {
                [LocalProcessParameterKeys.Executable] = CommandShellPath,
                [LocalProcessParameterKeys.ArgumentPrefix + "0"] = OperatingSystem.IsWindows() ? "/c" : "-c",
                [LocalProcessParameterKeys.ArgumentPrefix + "1"] = "exit 9"
            },
            contractVersion: 2);

        var result = await backend.StartAsync(job);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.Message.Should().Contain("selected argument prefix").And.Contain("requires version 2");
    }

    [Fact]
    public async Task StartAsync_EmptyPrefixWithoutSelfContainedAssertionRejectsLauncher()
    {
        const string launcher = "env";
        using var backend = CreateBackend(workerContracts:
        [
            new LocalProcessWorkerContractOptions
            {
                Executable = launcher,
                MaxSupportedContractVersion = RasterSourceContract.JobContractVersion
            }
        ]);
        var job = CreateJob(
            "job-env-launcher-v2",
            new Dictionary<string, string>
            {
                [LocalProcessParameterKeys.Executable] = launcher,
                [LocalProcessParameterKeys.ArgumentPrefix + "0"] = "old-v1-worker"
            },
            contractVersion: 2);

        var result = await backend.StartAsync(job);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.Message.Should().Contain("selected argument prefix").And.Contain("requires version 2");
    }

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
        capabilities.MaxSupportedContractVersion.Should().Be(RasterSourceContract.JobContractVersion);
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
    public async Task Resubmit_AfterTerminalObservation_RelaunchesSameOperation()
    {
        using var backend = CreateBackend();
        var job = CreateJob("job-retry", new Dictionary<string, string>
        {
            [LocalProcessParameterKeys.Executable] = SleepPath,
            // Invalid interval => sleep exits non-zero, driving the job to Failed.
            [LocalProcessParameterKeys.ArgumentPrefix + "0"] = "not-a-number"
        });

        var firstStart = await backend.StartAsync(job);
        firstStart.Status.Should().Be(ExecutionJobStatus.Running);

        var running = job with { Status = ExecutionJobStatus.Running, ProviderOperationId = firstStart.ProviderOperationId };
        var terminal = await WaitForTerminalAsync(backend, running);
        terminal.Status.Should().Be(ExecutionJobStatus.Failed);

        // The reconciler requeues the same OperationId. Before the eviction fix this re-entered Launch,
        // collided on the still-tracked terminal execution ("already tracked"), and was reported as a
        // bogus launch failure — so in-host retries never actually relaunched.
        var resubmit = await backend.StartAsync(job with { Status = ExecutionJobStatus.Queued });

        resubmit.Status.Should().Be(ExecutionJobStatus.Running);
        resubmit.Message.Should().NotContain("already tracked");
        resubmit.ProviderOperationId.Should().StartWith(LocalProcessPoolBatchComputeBackend.LaunchedMarkerPrefix);

        // Drain the relaunched attempt so the backend disposes cleanly.
        await WaitForTerminalAsync(backend, job with
        {
            Status = ExecutionJobStatus.Running,
            ProviderOperationId = resubmit.ProviderOperationId
        });
    }

    [Fact]
    public async Task ObserveAsync_AfterTerminalEviction_EchoesPersistedTerminalStatus()
    {
        using var backend = CreateBackend();
        var job = CreateJob("job-echo", new Dictionary<string, string>
        {
            [LocalProcessParameterKeys.Executable] = SleepPath,
            [LocalProcessParameterKeys.ArgumentPrefix + "0"] = "0"
        });

        var start = await backend.StartAsync(job);
        var running = job with { Status = ExecutionJobStatus.Running, ProviderOperationId = start.ProviderOperationId };

        // First terminal observation reports Succeeded and evicts the tracking entry.
        var terminal = await WaitForTerminalAsync(backend, running);
        terminal.Status.Should().Be(ExecutionJobStatus.Succeeded);

        // A later poll with the persisted terminal record must NOT flip Succeeded to a "process lost"
        // Failed just because the execution was evicted.
        var afterEviction = await backend.ObserveAsync(job with
        {
            Status = ExecutionJobStatus.Succeeded,
            ProviderOperationId = start.ProviderOperationId
        });

        afterEviction.Status.Should().Be(ExecutionJobStatus.Succeeded);
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

    private static LocalProcessPoolBatchComputeBackend CreateBackend(
        int maxConcurrent = 2,
        IReadOnlyList<LocalProcessWorkerContractOptions>? workerContracts = null,
        IReadOnlyDictionary<string, int>? executableContracts = null)
    {
        IReadOnlyList<LocalProcessWorkerContractOptions> contracts;
        if (workerContracts is not null)
        {
            contracts = workerContracts;
        }
        else if (executableContracts is not null)
        {
            contracts = executableContracts.Select(contract => new LocalProcessWorkerContractOptions
            {
                Executable = contract.Key,
                MaxSupportedContractVersion = contract.Value
            }).ToList();
        }
        else
        {
            contracts =
            [
                new LocalProcessWorkerContractOptions
                {
                    Executable = SleepPath,
                    IsSelfContained = true,
                    MaxSupportedContractVersion = RasterSourceContract.JobContractVersion
                }
            ];
        }

        return new LocalProcessPoolBatchComputeBackend(
            Options.Create(new LocalProcessPoolOptions
            {
                MaxConcurrentProcesses = maxConcurrent,
                WorkerContracts = [.. contracts]
            }),
            NullLogger<LocalProcessPoolBatchComputeBackend>.Instance);
    }

    private static ExecutionJobRecord CreateJob(
        string operationId,
        IReadOnlyDictionary<string, string> parameters,
        int contractVersion = 1)
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
                ContractVersion = contractVersion,
                Parameters = parameters
            }
        };
    }
}
