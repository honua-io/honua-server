// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

/// <summary>
/// Decides the process exit code an execution worker reports to its compute provider, after the
/// host has stopped. The job outcome itself always travels through the durable job store; the exit
/// code only tells the provider whether its own retry machinery should treat this run as failed.
/// </summary>
/// <remarks>
/// The rule is "succeed whenever the durable record no longer depends on this provider job". A
/// terminal record, a record handed back for resubmission, or a record that has moved to another
/// attempt exits 0, so a provider-native retry (for example AWS Batch <c>retryStrategy</c>) never
/// competes with the serving host's reconciler for the next attempt. Only a record this launch still
/// owns without an outcome — the worker could not run or report it — exits nonzero, so the
/// provider's failure drives the reconciler's retry instead of a misleading success.
/// </remarks>
internal sealed partial class ExecutionWorkerExitState(
    ExecutionWorkerLaunch launch,
    IExecutionJobStore jobStore,
    ILogger<ExecutionWorkerExitState> logger)
{
    private static readonly TimeSpan FinalReadTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Reads the final durable state and returns the exit code.</summary>
    public async Task<int> ResolveAsync()
    {
        ExecutionJobRecord? job;
        try
        {
            using var timeout = new CancellationTokenSource(FinalReadTimeout);
            job = await jobStore.GetAsync(launch.OperationId, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Deliberately broad: an unreadable store must still let the worker exit, and nonzero
            // lets the provider's failure drive the reconciler.
            Log.FinalReadFailed(logger, launch.OperationId, ex);
            return 1;
        }

        var exitCode = Resolve(launch, job);
        Log.ExitCodeResolved(logger, launch.OperationId, job?.Status.ToString() ?? "missing", exitCode);
        return exitCode;
    }

    internal static int Resolve(ExecutionWorkerLaunch launch, ExecutionJobRecord? job)
    {
        if (job is null)
        {
            return 1;
        }

        if (ExecutionJobReconciler.IsTerminal(job.Status))
        {
            return 0;
        }

        if (launch.ExecutionAttempt is { } attempt && job.AttemptCount != attempt)
        {
            return 0;
        }

        var handedBack = job.Status == ExecutionJobStatus.Queued
            && job.ClaimedBy is null
            && string.IsNullOrEmpty(job.ProviderOperationId)
            && job.NextRetryAt.HasValue;
        return handedBack ? 0 : 1;
    }

    private static partial class Log
    {
        [LoggerMessage(9491, LogLevel.Information,
            "Execution worker leaves operation {OperationId} in durable status {Status}; exit code {ExitCode}")]
        public static partial void ExitCodeResolved(ILogger logger, string operationId, string status, int exitCode);

        [LoggerMessage(9493, LogLevel.Warning, "Execution worker could not read the final state of operation {OperationId}")]
        public static partial void FinalReadFailed(ILogger logger, string operationId, Exception exception);
    }
}

/// <summary>
/// Drives a run-to-completion execution worker: names the composition profile and the assigned
/// operation at startup, waits for the shared execution loop to run (or refuse) that one
/// operation, and stops the host. The exit code is resolved by <see cref="ExecutionWorkerExitState"/>
/// once the host (and with it the execution loop) has stopped.
/// </summary>
internal sealed partial class ExecutionWorkerLifetimeService(
    ExecutionWorkerLaunch launch,
    AssignedExecutionJobQueue assignedQueue,
    IHostApplicationLifetime applicationLifetime,
    ILogger<ExecutionWorkerLifetimeService> logger) : BackgroundService
{
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        Log.ProfileSelected(
            logger,
            nameof(HostCompositionProfile.ExecutionWorker),
            launch.OperationId,
            launch.ExecutionAttempt?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "(unspecified)",
            launch.JobKind?.ToString() ?? "(unspecified)",
            launch.WorkloadName ?? launch.WorkloadId ?? "(unspecified)",
            launch.ContractVersion);
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var outcome = await assignedQueue.Completion.WaitAsync(stoppingToken).ConfigureAwait(false);
            Log.WorkerFinished(logger, launch.OperationId, outcome.ToString());
            applicationLifetime.StopApplication();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The provider is stopping the worker underneath the job; the execution loop (stopped
            // after this service) cancels it and hands the attempt back for resubmission.
            assignedQueue.MarkStopped();
            Log.WorkerInterrupted(logger, launch.OperationId);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(9490, LogLevel.Information,
            "Host composition profile {Profile}: running only execution job {OperationId} attempt {Attempt} (kind {JobKind}, workload {Workload}, contract v{ContractVersion}); "
            + "job dispatch, batch submission, control-plane reconcilers, the proposal gateway, scheduled background services and the HTTP surface are not composed")]
        public static partial void ProfileSelected(
            ILogger logger,
            string profile,
            string operationId,
            string attempt,
            string jobKind,
            string workload,
            int contractVersion);

        [LoggerMessage(9496, LogLevel.Information, "Execution worker finished operation {OperationId}: claim outcome {Outcome}")]
        public static partial void WorkerFinished(ILogger logger, string operationId, string outcome);

        [LoggerMessage(9492, LogLevel.Warning,
            "Execution worker stopped before operation {OperationId} finished; the attempt is handed back for resubmission")]
        public static partial void WorkerInterrupted(ILogger logger, string operationId);
    }
}
