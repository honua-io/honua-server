// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

/// <summary>
/// How an execution worker's single assigned operation was resolved.
/// </summary>
internal enum ExecutionWorkerClaimOutcome
{
    /// <summary>The worker claimed the assigned job and the execution loop has run it.</summary>
    Executed,

    /// <summary>The job was already terminal; there is nothing to run.</summary>
    AlreadyTerminal,

    /// <summary>No durable record exists for the assigned operation.</summary>
    NotFound,

    /// <summary>
    /// The durable record has moved past the attempt this process was launched for (it was handed
    /// back for resubmission, or resubmitted), so this launch is stale and must not run.
    /// </summary>
    StaleLaunch,

    /// <summary>Another live worker holds the claim on the launched attempt.</summary>
    OwnedByAnotherWorker,

    /// <summary>The launch environment names a different job kind than the durable record.</summary>
    LaunchMismatch,

    /// <summary>
    /// This image cannot run the job (unsupported kind, runtime profile or contract version); the
    /// worker recorded a terminal failure because another attempt on the same image cannot succeed.
    /// </summary>
    Unrunnable,

    /// <summary>
    /// The durable job store could not be read or written, the submission never became visible,
    /// or the host stopped first.
    /// </summary>
    StoreUnavailable,
}

/// <summary>
/// The <see cref="IJobQueue"/> an execution worker composes in place of the shared Redis queue.
/// It hands the shared <see cref="JobExecutionService"/> loop exactly one operation — the attempt the
/// batch compute backend launched this process for — so the worker runs the same claim, heartbeat,
/// artifact-publication fence and terminal finalization as an in-process run, but can never claim
/// another job from the shared queue.
/// </summary>
/// <remarks>
/// <para>
/// The claim does not count an attempt: the provider submission that launched this worker already
/// did (<c>ExecutionJobSubmissionHelper.StartOnRemoteBackendAsync</c>). When the launch carries
/// <see cref="ExecutionWorkerLaunch.ExecutionAttempt"/> the claim is fenced to that attempt, so a
/// stale container (or one the provider retried natively) can never run a newer attempt.
/// </para>
/// <para>
/// Retries are owned by the serving host's execution-job reconciler, which resubmits a record the
/// worker hands back as <c>Queued</c> with no provider marker and a retry time. A worker never
/// submits work, so <see cref="EnqueueAsync"/> fails loudly.
/// </para>
/// <para>
/// <see cref="JobExecutionService"/> processes a claim to completion before it polls again, so the
/// next <see cref="TryClaimAsync"/> call after a claim marks the assigned run finished and completes
/// <see cref="Completion"/>.
/// </para>
/// </remarks>
internal sealed partial class AssignedExecutionJobQueue : IJobQueue
{
    internal const string ClaimedPhase = "Claimed by execution worker";
    private const int MaxClaimAttempts = 3;
    private const int MaxSubmissionWaitPolls = 24;
    private const int Idle = 0;
    private const int Claimed = 1;
    private const int Finished = 2;

    private readonly ExecutionWorkerLaunch _launch;
    private readonly IExecutionJobStore _jobStore;
    private readonly IServiceProvider? _services;
    private readonly ILogger<AssignedExecutionJobQueue> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TaskCompletionSource<ExecutionWorkerClaimOutcome> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _state = Idle;
    private int _failedClaimAttempts;
    private int _submissionWaitPolls;

    public AssignedExecutionJobQueue(
        ExecutionWorkerLaunch launch,
        IExecutionJobStore jobStore,
        ILogger<AssignedExecutionJobQueue> logger,
        IServiceProvider? services = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(jobStore);
        ArgumentNullException.ThrowIfNull(logger);
        _launch = launch;
        _jobStore = jobStore;
        _logger = logger;
        _services = services;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The operation this worker was launched for.</summary>
    public string OperationId => _launch.OperationId;

    /// <summary>Completes once the assigned operation has been run or refused.</summary>
    public Task<ExecutionWorkerClaimOutcome> Completion => _completion.Task;

    public Task EnqueueAsync(
        string operationId,
        OperationPriority priority = OperationPriority.Normal,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(
            $"An execution worker runs only its assigned operation '{_launch.OperationId}'; it cannot "
            + $"enqueue operation '{operationId}'. Job dispatch belongs to the serving host.");

    public async Task<string?> TryClaimAsync(
        string workerId,
        IReadOnlySet<ExecutionJobKind>? acceptedKinds = null,
        IReadOnlySet<string>? acceptedRuntimeProfiles = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

        var state = Volatile.Read(ref _state);
        if (state == Claimed)
        {
            // The execution loop only polls again after it has processed the claim it was given.
            Volatile.Write(ref _state, Finished);
            _completion.TrySetResult(ExecutionWorkerClaimOutcome.Executed);
            return null;
        }

        if (state == Finished)
        {
            return null;
        }

        ExecutionWorkerClaimOutcome? outcome;
        try
        {
            outcome = await TryClaimAssignedAsync(workerId, acceptedKinds, acceptedRuntimeProfiles, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
        {
            if (++_failedClaimAttempts < MaxClaimAttempts)
            {
                // Let the execution loop log the fault and poll again after its interval.
                throw;
            }

            Log.ClaimStoreUnavailable(_logger, _launch.OperationId, ex);
            outcome = ExecutionWorkerClaimOutcome.StoreUnavailable;
        }

        if (outcome is null)
        {
            // The submitting server has not recorded this attempt yet; poll again.
            if (++_submissionWaitPolls < MaxSubmissionWaitPolls)
            {
                return null;
            }

            Log.SubmissionNeverRecorded(_logger, _launch.OperationId, _launch.ExecutionAttempt ?? 0);
            outcome = ExecutionWorkerClaimOutcome.StoreUnavailable;
        }

        if (outcome == ExecutionWorkerClaimOutcome.Executed)
        {
            Volatile.Write(ref _state, Claimed);
            return _launch.OperationId;
        }

        Volatile.Write(ref _state, Finished);
        _completion.TrySetResult(outcome.Value);
        return null;
    }

    public async Task RequeueAsync(
        string operationId,
        OperationPriority priority = OperationPriority.Normal,
        TimeSpan? visibleAfter = null,
        CancellationToken cancellationToken = default)
    {
        // The execution loop has already written the record back to Queued with no provider id. The
        // serving host's reconciler resubmits such a record only when it carries a retry time; without
        // one (an immediate handback such as a provider interruption) it would read the record as an
        // orphaned submission of the attempt that just ended. Stamp the retry time so the next attempt
        // is a fresh provider submission.
        for (var attempt = 0; attempt < MaxClaimAttempts; attempt++)
        {
            var job = await _jobStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (job is null
                || job.Status != ExecutionJobStatus.Queued
                || job.ClaimedBy is not null
                || !string.IsNullOrEmpty(job.ProviderOperationId)
                || job.NextRetryAt.HasValue)
            {
                break;
            }

            var retry = job with { NextRetryAt = _timeProvider.GetUtcNow(), UpdatedAt = _timeProvider.GetUtcNow() };
            if (await _jobStore.TrySetAsync(retry, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                break;
            }
        }

        Log.RequeueHandedToReconciler(_logger, operationId);
    }

    public Task RemoveAsync(string operationId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<long> GetQueueDepthAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(0L);

    /// <summary>
    /// Completes <see cref="Completion"/> when the host stops before the execution loop polled
    /// again (for example a Spot interruption while the assigned job was running).
    /// </summary>
    internal void MarkStopped()
    {
        var previous = Interlocked.Exchange(ref _state, Finished);
        _completion.TrySetResult(previous == Claimed
            ? ExecutionWorkerClaimOutcome.Executed
            : ExecutionWorkerClaimOutcome.StoreUnavailable);
    }

    /// <returns>The outcome, or <see langword="null"/> when the launched submission is not recorded yet.</returns>
    private async Task<ExecutionWorkerClaimOutcome?> TryClaimAssignedAsync(
        string workerId,
        IReadOnlySet<ExecutionJobKind>? acceptedKinds,
        IReadOnlySet<string>? acceptedRuntimeProfiles,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxClaimAttempts; attempt++)
        {
            var job = await _jobStore.GetAsync(_launch.OperationId, cancellationToken).ConfigureAwait(false);
            if (job is null)
            {
                Log.AssignedJobNotFound(_logger, _launch.OperationId);
                return ExecutionWorkerClaimOutcome.NotFound;
            }

            if (ExecutionJobReconciler.IsTerminal(job.Status))
            {
                Log.AssignedJobAlreadyTerminal(_logger, _launch.OperationId, job.Status.ToString());
                return ExecutionWorkerClaimOutcome.AlreadyTerminal;
            }

            if (_launch.JobKind is { } launchedKind && launchedKind != job.Spec.Kind)
            {
                Log.LaunchKindMismatch(_logger, _launch.OperationId, launchedKind.ToString(), job.Spec.Kind.ToString());
                return ExecutionWorkerClaimOutcome.LaunchMismatch;
            }

            if (_launch.ExecutionAttempt is { } launchedAttempt)
            {
                if (job.AttemptCount > launchedAttempt)
                {
                    Log.AssignedJobStale(_logger, _launch.OperationId, launchedAttempt, job.AttemptCount);
                    return ExecutionWorkerClaimOutcome.StaleLaunch;
                }

                if (job.AttemptCount < launchedAttempt)
                {
                    // The provider started this container before the submitting server recorded the
                    // submission (its post-start write). Claiming now would race that write.
                    return null;
                }
            }

            if (job.Status == ExecutionJobStatus.Queued && !ExecutionJobCancellationHelper.HasSubmittedProviderMarker(job))
            {
                Log.AssignedJobStale(_logger, _launch.OperationId, _launch.ExecutionAttempt ?? 0, job.AttemptCount);
                return ExecutionWorkerClaimOutcome.StaleLaunch;
            }

            var refusal = DescribeUnrunnable(job, acceptedKinds, acceptedRuntimeProfiles);
            if (refusal is not null)
            {
                return await FailUnrunnableAsync(job, refusal, cancellationToken).ConfigureAwait(false);
            }

            var now = _timeProvider.GetUtcNow();
            if (!string.IsNullOrEmpty(job.ClaimedBy)
                && !string.Equals(job.ClaimedBy, workerId, StringComparison.Ordinal)
                && (job.LastHeartbeatAt ?? job.ClaimedAt) is { } lastHeartbeat
                && !(job.HeartbeatPolicy ?? JobHeartbeatPolicy.Default).IsExpired(lastHeartbeat, now))
            {
                Log.AssignedJobOwnedElsewhere(_logger, _launch.OperationId, job.ClaimedBy);
                return ExecutionWorkerClaimOutcome.OwnedByAnotherWorker;
            }

            var claimed = job with
            {
                // JobExecutionService promotes Provisioning to Running; a record the reconciler
                // already observed as Running stays Running.
                Status = job.Status == ExecutionJobStatus.Running ? ExecutionJobStatus.Running : ExecutionJobStatus.Provisioning,
                ClaimedBy = workerId,
                ClaimedAt = now,
                LastHeartbeatAt = now,
                UpdatedAt = now,
                CurrentPhase = ClaimedPhase,
                NextRetryAt = null,
            };

            if (await _jobStore.TrySetAsync(claimed, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                ControlPlaneTelemetry.RecordExecutionTransition(job, claimed);
                Log.AssignedJobClaimed(_logger, _launch.OperationId, workerId);
                return ExecutionWorkerClaimOutcome.Executed;
            }

            // A concurrent provider observation (or cancellation stamp) moved the version; re-read.
        }

        throw new InvalidOperationException(
            $"Execution worker could not claim operation '{_launch.OperationId}' after {MaxClaimAttempts} version conflicts.");
    }

    private string? DescribeUnrunnable(
        ExecutionJobRecord job,
        IReadOnlySet<ExecutionJobKind>? acceptedKinds,
        IReadOnlySet<string>? acceptedRuntimeProfiles)
    {
        var contractVersion = Math.Max(job.Spec.ContractVersion, _launch.ContractVersion);
        if (contractVersion > ExecutionWorkerLaunch.MaxSupportedContractVersion)
        {
            return $"The execution worker supports job contract version {ExecutionWorkerLaunch.MaxSupportedContractVersion}, "
                + $"but the job requires version {contractVersion}.";
        }

        if (acceptedKinds is not null && !acceptedKinds.Contains(job.Spec.Kind))
        {
            return $"The execution worker image has no executor for job kind '{job.Spec.Kind}'.";
        }

        if (!RuntimeProfiles.CanClaim(acceptedRuntimeProfiles, job.Spec.RuntimeProfile))
        {
            return $"The execution worker image cannot run runtime profile '{RuntimeProfiles.Normalize(job.Spec.RuntimeProfile)}'.";
        }

        return null;
    }

    private async Task<ExecutionWorkerClaimOutcome> FailUnrunnableAsync(
        ExecutionJobRecord job,
        string reason,
        CancellationToken cancellationToken)
    {
        Log.AssignedJobUnrunnable(_logger, _launch.OperationId, reason);
        var now = _timeProvider.GetUtcNow();
        var failed = job with
        {
            Status = ExecutionJobStatus.Failed,
            UpdatedAt = now,
            CompletedAt = now,
            CurrentPhase = "Failed",
            ErrorMessage = reason,
            NextRetryAt = null,
        };

        if (!await _jobStore.TrySetAsync(failed, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            // Someone else moved the record; the next poll (or the provider outcome) decides.
            throw new InvalidOperationException(
                $"Execution worker could not record the refusal of operation '{_launch.OperationId}'.");
        }

        ControlPlaneTelemetry.RecordExecutionTransition(job, failed);

        // Fan out exactly as the shared loop does for a terminal transition, so the GP result
        // package and progress projection see the failure instead of an awaiting job.
        foreach (var callback in _services?.GetServices<IJobTerminalCallback>() ?? [])
        {
            try
            {
                await callback.OnTerminalAsync(failed, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Deliberately broad: one failing callback must not stop the rest; the terminal
                // record is already durable.
                Log.TerminalCallbackFailed(_logger, _launch.OperationId, ex);
            }
        }

        return ExecutionWorkerClaimOutcome.Unrunnable;
    }

    private static partial class Log
    {
        [LoggerMessage(9481, LogLevel.Information, "Execution worker claimed its assigned operation {OperationId} as {WorkerId}")]
        public static partial void AssignedJobClaimed(ILogger logger, string operationId, string workerId);

        [LoggerMessage(9482, LogLevel.Error, "Execution worker found no durable record for its assigned operation {OperationId}")]
        public static partial void AssignedJobNotFound(ILogger logger, string operationId);

        [LoggerMessage(9483, LogLevel.Information, "Execution worker assigned operation {OperationId} is already {Status}; nothing to run")]
        public static partial void AssignedJobAlreadyTerminal(ILogger logger, string operationId, string status);

        [LoggerMessage(9484, LogLevel.Warning, "Execution worker launch is stale: operation {OperationId} launched for attempt {LaunchedAttempt} but the durable record is at attempt {RecordAttempt} or queued for resubmission")]
        public static partial void AssignedJobStale(ILogger logger, string operationId, int launchedAttempt, int recordAttempt);

        [LoggerMessage(9485, LogLevel.Warning, "Execution worker refused operation {OperationId}: live claim held by {ClaimedBy}")]
        public static partial void AssignedJobOwnedElsewhere(ILogger logger, string operationId, string? claimedBy);

        [LoggerMessage(9486, LogLevel.Error, "Execution worker launch for operation {OperationId} names job kind {LaunchedKind} but the durable record is {RecordKind}")]
        public static partial void LaunchKindMismatch(ILogger logger, string operationId, string launchedKind, string recordKind);

        [LoggerMessage(9487, LogLevel.Error, "Execution worker cannot run operation {OperationId}: {Reason}")]
        public static partial void AssignedJobUnrunnable(ILogger logger, string operationId, string reason);

        [LoggerMessage(9488, LogLevel.Error, "Execution worker could not reach the durable job store to claim operation {OperationId}")]
        public static partial void ClaimStoreUnavailable(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9489, LogLevel.Information, "Execution worker handed operation {OperationId} back for a provider retry; the serving host reconciler resubmits it")]
        public static partial void RequeueHandedToReconciler(ILogger logger, string operationId);

        [LoggerMessage(9494, LogLevel.Error, "Execution worker for operation {OperationId} never saw attempt {Attempt} recorded by the submitting server")]
        public static partial void SubmissionNeverRecorded(ILogger logger, string operationId, int attempt);

        [LoggerMessage(9495, LogLevel.Warning, "Execution worker terminal callback failed for operation {OperationId}")]
        public static partial void TerminalCallbackFailed(ILogger logger, string operationId, Exception exception);
    }
}
