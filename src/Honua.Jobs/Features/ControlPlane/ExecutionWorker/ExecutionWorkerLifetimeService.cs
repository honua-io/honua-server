// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

/// <summary>
/// The process exit code an execution worker reports to the compute provider. The job outcome
/// itself always travels through the durable job store; the exit code only tells the provider
/// (and an operator reading its console) whether this run recorded a success.
/// </summary>
internal sealed class ExecutionWorkerExitState
{
    /// <summary>Nonzero until the worker has recorded a successful outcome.</summary>
    public int ExitCode { get; set; } = 1;
}

/// <summary>
/// Drives a run-to-completion execution worker: names the composition profile and the assigned
/// operation at startup, waits for the shared execution loop to run (or refuse) that one
/// operation, records the exit code and stops the host.
/// </summary>
internal sealed partial class ExecutionWorkerLifetimeService(
    ExecutionWorkerLaunch launch,
    AssignedExecutionJobQueue assignedQueue,
    IExecutionJobStore jobStore,
    ExecutionWorkerExitState exitState,
    IHostApplicationLifetime applicationLifetime,
    ILogger<ExecutionWorkerLifetimeService> logger) : BackgroundService
{
    private static readonly TimeSpan FinalReadTimeout = TimeSpan.FromSeconds(10);

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        Log.ProfileSelected(
            logger,
            nameof(HostCompositionProfile.ExecutionWorker),
            launch.OperationId,
            launch.JobKind?.ToString() ?? "(unspecified)",
            launch.WorkloadName ?? launch.WorkloadId ?? "(unspecified)",
            launch.ContractVersion);
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ExecutionWorkerClaimOutcome outcome;
        try
        {
            outcome = await assignedQueue.Completion.WaitAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping underneath the job (provider interruption). JobExecutionService
            // hands the attempt back for a provider retry; this run did not record a success.
            assignedQueue.MarkStopped();
            exitState.ExitCode = 1;
            Log.WorkerInterrupted(logger, launch.OperationId);
            return;
        }

        var finalStatus = await ReadFinalStatusAsync().ConfigureAwait(false);
        exitState.ExitCode = outcome switch
        {
            ExecutionWorkerClaimOutcome.Executed when finalStatus == ExecutionJobStatus.Succeeded => 0,
            ExecutionWorkerClaimOutcome.AlreadyTerminal => 0,
            _ => 1,
        };

        Log.WorkerFinished(
            logger,
            launch.OperationId,
            outcome.ToString(),
            finalStatus?.ToString() ?? "unknown",
            exitState.ExitCode);
        applicationLifetime.StopApplication();
    }

    private async Task<ExecutionJobStatus?> ReadFinalStatusAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(FinalReadTimeout);
            var job = await jobStore.GetAsync(launch.OperationId, timeout.Token).ConfigureAwait(false);
            return job?.Status;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Deliberately broad: the outcome is already durable (or not) — this read only picks
            // the exit code, and an unreadable store must still let the worker exit.
            Log.FinalReadFailed(logger, launch.OperationId, ex);
            return null;
        }
    }

    private static partial class Log
    {
        [LoggerMessage(9490, LogLevel.Information,
            "Host composition profile {Profile}: running only execution job {OperationId} (kind {JobKind}, workload {Workload}, contract v{ContractVersion}); "
            + "job dispatch, batch submission, control-plane reconcilers, the proposal gateway, scheduled background services and the HTTP surface are not composed")]
        public static partial void ProfileSelected(
            ILogger logger,
            string profile,
            string operationId,
            string jobKind,
            string workload,
            int contractVersion);

        [LoggerMessage(9491, LogLevel.Information,
            "Execution worker finished operation {OperationId}: claim outcome {Outcome}, durable status {Status}, exit code {ExitCode}")]
        public static partial void WorkerFinished(ILogger logger, string operationId, string outcome, string status, int exitCode);

        [LoggerMessage(9492, LogLevel.Warning,
            "Execution worker stopped before operation {OperationId} finished; the attempt is handed back for a provider retry")]
        public static partial void WorkerInterrupted(ILogger logger, string operationId);

        [LoggerMessage(9493, LogLevel.Warning, "Execution worker could not read the final state of operation {OperationId}")]
        public static partial void FinalReadFailed(ILogger logger, string operationId, Exception exception);
    }
}
