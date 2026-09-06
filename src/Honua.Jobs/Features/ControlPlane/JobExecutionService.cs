// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Licensing.Abstractions;

namespace Honua.ControlPlane;

/// <summary>
/// Worker-side hosted service that claims jobs from the queue, dispatches them
/// to the appropriate <see cref="IJobExecutor"/>, manages heartbeat pumping
/// during execution, and finalizes job state on completion or failure.
/// </summary>
internal sealed partial class JobExecutionService(
    IJobQueue jobQueue,
    IExecutionJobStore jobStore,
    IEnumerable<IJobExecutor> executors,
    ExecutionJobCancellationTokens cancellationTokens,
    IEnumerable<IJobTerminalCallback> terminalCallbacks,
    IExecutionLogStore? logStore,
    ILogger<JobExecutionService> logger,
    ILicenseOperationPolicy? licensePolicy = null) : BackgroundService
{
    private const string SafeExecutionFailureMessage = "Job execution failed.";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LogRetention = TimeSpan.FromDays(7);
    private static readonly TimeSpan DefaultPartitionLeaseDuration = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DefaultPartitionLeaseRenewInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DefaultPartitionLeaseContentionDelay = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _partitionLeaseDuration = DefaultPartitionLeaseDuration;
    private readonly TimeSpan _partitionLeaseRenewInterval = DefaultPartitionLeaseRenewInterval;
    private readonly TimeSpan _partitionLeaseContentionDelay = DefaultPartitionLeaseContentionDelay;

    internal JobExecutionService(
        IJobQueue jobQueue,
        IExecutionJobStore jobStore,
        IEnumerable<IJobExecutor> executors,
        ExecutionJobCancellationTokens cancellationTokens,
        IEnumerable<IJobTerminalCallback> terminalCallbacks,
        IExecutionLogStore? logStore,
        ILogger<JobExecutionService> logger,
        TimeSpan partitionLeaseDuration,
        TimeSpan partitionLeaseRenewInterval,
        TimeSpan partitionLeaseContentionDelay)
        : this(jobQueue, jobStore, executors, cancellationTokens, terminalCallbacks, logStore, logger)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(partitionLeaseDuration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(partitionLeaseRenewInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(partitionLeaseContentionDelay, TimeSpan.Zero);
        _partitionLeaseDuration = partitionLeaseDuration;
        _partitionLeaseRenewInterval = partitionLeaseRenewInterval;
        _partitionLeaseContentionDelay = partitionLeaseContentionDelay;
    }

    // Multiple executors can share a single ExecutionJobKind when they fence on
    // different runtime profiles — e.g. for ExecutionJobKind.Geoprocessing the lean
    // managed dispatcher (GeoprocessingDispatchJobExecutor), the custom-code dispatcher
    // (CustomCodeDispatchJobExecutor, "custom-code" profile), and the native GDAL
    // dispatcher (GdalDispatchJobExecutor, "native" profile) are all registered as
    // IJobExecutor. Group by Kind (instead of ToDictionary, which throws on the
    // duplicate Kind) and disambiguate by the job's runtime profile at dispatch time.
    private readonly Dictionary<ExecutionJobKind, IJobExecutor[]> _executorsByKind =
        executors
            .GroupBy(e => e.Kind)
            .ToDictionary(g => g.Key, g => g.ToArray());

    private readonly HashSet<ExecutionJobKind> _acceptedKinds =
        new(executors.Select(e => e.Kind));

    // Aggregated runtime-profile claim fence. The union of every registered
    // executor's accepted profiles. Each executor defaults to managed/default only
    // (RuntimeProfiles.DefaultAccepted), so a lean worker composed entirely of
    // default executors accepts only managed/default jobs and can never claim a
    // native job. A worker whose executors declare { "native" } accepts native jobs.
    // This set is always non-empty for a worker with at least one executor and is
    // passed to the queue claim so the fence is enforced at the claim boundary.
    private readonly IReadOnlySet<string> _acceptedRuntimeProfiles = BuildAcceptedProfiles(executors);

    private static HashSet<string> BuildAcceptedProfiles(IEnumerable<IJobExecutor> executors)
    {
        var profiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var executor in executors)
        {
            foreach (var profile in executor.AcceptedRuntimeProfiles)
            {
                profiles.Add(profile);
            }
        }

        return profiles;
    }

    /// <summary>
    /// Selects the executor for a claimed job by kind, disambiguating among
    /// executors that share the kind via the runtime-profile claim fence. When a
    /// single executor is registered for the kind it is returned directly (the
    /// claim-side profile fence already guaranteed this worker accepts the job's
    /// profile); when several share the kind, the one whose
    /// <see cref="IJobExecutor.AcceptedRuntimeProfiles"/> can claim the job's
    /// effective profile is chosen.
    /// </summary>
    private IJobExecutor? ResolveExecutor(ExecutionJobKind kind, string? runtimeProfile)
    {
        if (!_executorsByKind.TryGetValue(kind, out var candidates) || candidates.Length == 0)
        {
            return null;
        }

        if (candidates.Length == 1)
        {
            return candidates[0];
        }

        return candidates.FirstOrDefault(candidate =>
            RuntimeProfiles.CanClaim(candidate.AcceptedRuntimeProfiles, runtimeProfile));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workerId = GenerateWorkerId();

        if (_acceptedKinds.Count == 0)
        {
            Log.NoExecutorsRegistered(logger, workerId);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Expected: host shutdown cancelled the infinite delay for this
                // executor-less worker. Nothing to clean up — fall through to stop logging.
            }

            Log.WorkerStopped(logger, workerId);
            return;
        }

        Log.WorkerStarted(logger, workerId);

        while (!stoppingToken.IsCancellationRequested)
        {
            string? claimedId = null;

            try
            {
                claimedId = await jobQueue.TryClaimAsync(
                    workerId, _acceptedKinds, _acceptedRuntimeProfiles, stoppingToken).ConfigureAwait(false);

                if (claimedId != null)
                {
                    await ProcessJobAsync(claimedId, workerId, stoppingToken).ConfigureAwait(false);
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // If shutdown arrived during the pre-execution phase of
                // ProcessJobAsync, the job is still claimed but was never
                // handed to the executor try/catch that handles shutdown
                // requeue. Force-requeue here so the job returns to the
                // pending queue immediately instead of waiting for heartbeat
                // expiry.
                if (claimedId != null)
                {
                    try
                    {
                        var job = await jobStore.GetAsync(claimedId, CancellationToken.None).ConfigureAwait(false);
                        if (job != null && !IsTerminalOrNotOwnedBy(job, workerId))
                        {
                            await AbandonJobAsync(job, workerId, "Worker shutdown.",
                                CancellationToken.None, forceRequeue: true).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        // Deliberately broad: this is best-effort shutdown cleanup for a
                        // single claimed job; a failure here must not prevent the worker
                        // from breaking out of the claim loop, and the stale-claim
                        // reconciler recovers the job if this cleanup fails.
                        Log.PreExecShutdownCleanupFailed(logger, claimedId, ex);
                    }

                    cancellationTokens.Remove(claimedId, workerId);
                }

                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Deliberately broad: one failed claim attempt (store/queue outage) must
                // not crash the worker's claim loop; log and retry after the poll delay.
                Log.ClaimLoopError(logger, ex);
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        Log.WorkerStopped(logger, workerId);
    }

    private async Task ProcessJobAsync(string operationId, string workerId, CancellationToken stoppingToken)
    {
        var job = await jobStore.GetAsync(operationId, stoppingToken).ConfigureAwait(false);
        if (job == null)
        {
            Log.JobNotFoundDuringExecution(logger, operationId);
            await jobQueue.RemoveAsync(operationId, stoppingToken).ConfigureAwait(false);
            return;
        }

        var executor = ResolveExecutor(job.Spec.Kind, job.Spec.RuntimeProfile);
        if (executor is null)
        {
            Log.NoExecutorForKind(logger, operationId, job.Spec.Kind.ToString());
            await AbandonJobAsync(job, workerId, "No executor registered for job kind.", stoppingToken).ConfigureAwait(false);
            return;
        }

        // Register the per-job CTS before the ownership re-check and Running
        // transition so that operator cancellation arriving from this point is
        // delivered through the token rather than as a direct store write that
        // the subsequent Running transition could overwrite.
        var timeoutPolicy = job.TimeoutPolicy ?? JobTimeoutPolicy.Default;
        using var timeoutCts = new CancellationTokenSource(timeoutPolicy.MaxDuration);
        var licenseCancellation = licensePolicy?.OperationCancellation ?? CancellationToken.None;
        using var jobCts = cancellationTokens.CreateLinkedTokenSource(
            operationId, workerId, stoppingToken, timeoutCts.Token, licenseCancellation);

        // Re-read before promoting to Running to catch cancellations that arrived
        // after the claim but before the worker registered its CTS.
        job = await jobStore.GetAsync(operationId, stoppingToken).ConfigureAwait(false);
        if (job == null)
        {
            cancellationTokens.Remove(operationId, workerId);
            await jobQueue.RemoveAsync(operationId, stoppingToken).ConfigureAwait(false);
            return;
        }

        if (IsTerminalOrNotOwnedBy(job, workerId))
        {
            Log.TerminalStateSkipped(logger, operationId, job.Status.ToString());
            cancellationTokens.Remove(operationId, workerId);

            // Only remove the queue entry for terminal jobs. Requeued or reclaimed
            // jobs have a queue entry that belongs to the new attempt.
            if (IsTerminal(job.Status))
            {
                await jobQueue.RemoveAsync(operationId, stoppingToken).ConfigureAwait(false);
            }

            return;
        }

        // A durable cancellation signal may have been written by a remote API
        // host between the claim and the CTS registration. Honour it before
        // promoting to Running.
        if (job.CancellationRequestedAt.HasValue)
        {
            Log.JobCancelledByOperator(logger, operationId);
            await TerminateJobAsync(operationId, workerId, ExecutionJobStatus.Cancelled,
                "Cancelled by operator (durable signal).", CancellationToken.None).ConfigureAwait(false);
            cancellationTokens.Remove(operationId, workerId);
            return;
        }

        // Transition to Running. Use CAS to prevent overwriting a concurrent
        // cancel signal or terminal write that landed after the re-read above.
        var now = DateTimeOffset.UtcNow;
        var running = job with
        {
            Status = ExecutionJobStatus.Running,
            UpdatedAt = now,
            LastHeartbeatAt = now,
            CurrentPhase = "Running"
        };
        if (!await jobStore.TrySetAsync(running, cancellationToken: stoppingToken).ConfigureAwait(false))
        {
            Log.TerminalStateSkipped(logger, operationId, "CAS conflict on Running transition");
            cancellationTokens.Remove(operationId, workerId);
            return;
        }

        ControlPlaneTelemetry.RecordExecutionTransition(job, running);

        var partitionKey = running.Concurrency.RequiresExclusiveLease
            ? running.Concurrency.PartitionKey?.Trim()
            : null;
        var partitionLeaseId = string.IsNullOrWhiteSpace(partitionKey)
            ? null
            : BuildPartitionLeaseId(partitionKey);
        var partitionLeaseOwner = partitionLeaseId is null ? null : $"{workerId}:{operationId}";

        var partitionLeaseAcquired = true;
        if (partitionLeaseId is not null)
        {
            try
            {
                partitionLeaseAcquired = await jobStore.TryAcquireLeaseAsync(
                        partitionLeaseId,
                        partitionLeaseOwner!,
                        _partitionLeaseDuration,
                        stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.PartitionLeaseAcquisitionFailed(logger, operationId, partitionKey!, ex);
                partitionLeaseAcquired = false;
            }
        }

        if (!partitionLeaseAcquired)
        {
            Log.PartitionLeaseUnavailable(logger, operationId, partitionKey!);
            await AbandonJobAsync(
                    running,
                    workerId,
                    "Exclusive partition lease is held by another job.",
                    CancellationToken.None,
                    forceRequeue: true,
                    requeueDelayOverride: _partitionLeaseContentionDelay,
                    restoreClaimAttempt: true)
                .ConfigureAwait(false);
            cancellationTokens.Remove(operationId, workerId);
            return;
        }

        using var partitionLeaseLostCts = new CancellationTokenSource();
        using var partitionLeaseRenewalCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var partitionLeaseRenewalTask = partitionLeaseId is null
            ? Task.CompletedTask
            : RenewPartitionLeaseUntilCancelledAsync(
                operationId,
                partitionKey!,
                partitionLeaseId,
                partitionLeaseOwner!,
                partitionLeaseLostCts,
                jobCts,
                partitionLeaseRenewalCts.Token);

        // PA-159: the worker-side execution loop (claim -> executor dispatch -> terminal
        // finalize) had metrics and logging but no trace span, leaving the actual job
        // execution invisible in traces even though ControlPlaneTelemetry already provides
        // a StartExecutionActivity helper used by the submission-side backend calls.
        using var activity = ControlPlaneTelemetry.StartExecutionActivity(
            ControlPlaneTelemetry.Activities.ExecutionRun, "run", running);

        Log.JobExecutionStarted(logger, operationId, executor.Kind.ToString());

        // Create execution context with heartbeat pump. The claimed attempt number
        // fences artifact publication: a stale attempt cannot publish after requeue.
        using var context = new JobExecutionContext(
            operationId, workerId, jobStore, logStore, job.HeartbeatPolicy ?? JobHeartbeatPolicy.Default, jobCts, logger,
            running.AttemptCount, licenseCancellation);

        // Start heartbeat pump in background.
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(jobCts.Token);
        var heartbeatTask = context.RunHeartbeatPumpAsync(heartbeatCts.Token);

        // Stops the heartbeat pump and waits for it to finish so that no
        // in-flight heartbeat write can clobber the terminal-state update.
        async Task StopHeartbeatPumpAsync()
        {
            await heartbeatCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await heartbeatTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation reaches the pump.
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Deliberately broad: the heartbeat pump is a background side effect of
                // execution; a fault here must not prevent finalization from proceeding
                // based on the executor's own outcome.
                Log.HeartbeatPumpFaulted(logger, operationId, ex);
            }
        }

        try
        {
            licenseCancellation.ThrowIfCancellationRequested();
            var result = await executor.ExecuteAsync(running, context, jobCts.Token).ConfigureAwait(false);
            await StopHeartbeatPumpAsync().ConfigureAwait(false);
            licenseCancellation.ThrowIfCancellationRequested();

            // Executors are expected to observe cancellation, but a late cancellation can race a
            // normal return and third-party executors may ignore the token. Never finalize from a
            // lease-invalid execution merely because no OperationCanceledException was thrown.
            if (timeoutCts.IsCancellationRequested)
            {
                activity?.SetStatus(ActivityStatusCode.Error, "Execution timed out.");
                Log.JobTimedOut(logger, operationId, timeoutPolicy.MaxDuration);
                await TerminateJobAsync(operationId, workerId, ExecutionJobStatus.Failed,
                    $"Execution timed out after {timeoutPolicy.MaxDuration}.", CancellationToken.None)
                    .ConfigureAwait(false);
                return;
            }

            if (stoppingToken.IsCancellationRequested)
            {
                activity?.SetStatus(ActivityStatusCode.Error, "Worker shutdown.");
                await AbandonJobAsync(running, workerId, "Worker shutdown.",
                    CancellationToken.None, forceRequeue: true).ConfigureAwait(false);
                return;
            }

            var partitionLeaseIsCurrent = !partitionLeaseLostCts.IsCancellationRequested
                && await VerifyPartitionLeaseOwnershipAsync(
                        operationId,
                        partitionKey,
                        partitionLeaseId,
                        partitionLeaseOwner)
                    .ConfigureAwait(false);
            if (!partitionLeaseIsCurrent)
            {
                activity?.SetStatus(ActivityStatusCode.Error, "Exclusive partition lease lost.");
                await AbandonJobAsync(
                        running,
                        workerId,
                        "Exclusive partition lease was lost during execution.",
                        CancellationToken.None,
                        forceRequeue: true,
                        requeueDelayOverride: _partitionLeaseContentionDelay)
                    .ConfigureAwait(false);
                return;
            }

            if (result.Status == ExecutionJobStatus.Succeeded)
            {
                activity?.SetStatus(ActivityStatusCode.Ok);
                var finalized = await FinalizeJobAsync(
                        operationId,
                        workerId,
                        result,
                        partitionLeaseId,
                        partitionLeaseOwner,
                        licenseCancellation)
                    .ConfigureAwait(false);
                if (!finalized)
                {
                    activity?.SetStatus(ActivityStatusCode.Error, "Exclusive partition lease lost before finalization.");
                    await AbandonJobAsync(
                            running,
                            workerId,
                            "Exclusive partition lease was lost before finalization.",
                            CancellationToken.None,
                            forceRequeue: true,
                            requeueDelayOverride: _partitionLeaseContentionDelay)
                        .ConfigureAwait(false);
                }
            }
            else
            {
                activity?.SetStatus(ActivityStatusCode.Error, result.ErrorMessage);
                // Executor returned failure — route through retry policy.
                // Thread executor warnings so they are persisted on terminal
                // failure and logged to structured execution logs on retry.
                if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                {
                    Log.JobExecutorReturnedFailure(logger, operationId, result.ErrorMessage);
                }

                // Surface the executor-authored failure detail on the durable job
                // record instead of collapsing it to the generic safe message
                // (#2348). An executor's JobExecutionResult.Failed(...) message is
                // a curated, client-safe reason (e.g. "missing required input",
                // "sink.honua-layer is unavailable in this deployment"), unlike a
                // raw thrown exception; persisting it makes GP jobs diagnosable
                // through the OGC/GPServer status surfaces. Falls back to the
                // generic message only when the executor supplied no detail.
                var failureReason = string.IsNullOrWhiteSpace(result.ErrorMessage)
                    ? SafeExecutionFailureMessage
                    : result.ErrorMessage!;

                await AbandonJobAsync(running, workerId, failureReason,
                    CancellationToken.None, result.Warnings).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (licenseCancellation.IsCancellationRequested && ex is not OutOfMemoryException)
        {
            await StopHeartbeatPumpAsync().ConfigureAwait(false);
            await TerminateJobAsync(operationId, workerId, ExecutionJobStatus.Failed,
                "license expired", CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await StopHeartbeatPumpAsync().ConfigureAwait(false);

            // Timeout takes precedence over shutdown: a timed-out job must
            // fail terminally per ADR-0031, even when shutdown also fired.
            if (timeoutCts.IsCancellationRequested)
            {
                activity?.SetStatus(ActivityStatusCode.Error, "Execution timed out.");
                Log.JobTimedOut(logger, operationId, timeoutPolicy.MaxDuration);
                await TerminateJobAsync(operationId, workerId, ExecutionJobStatus.Failed,
                    $"Execution timed out after {timeoutPolicy.MaxDuration}.", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            else
            {
                activity?.SetStatus(ActivityStatusCode.Error, "Worker shutdown.");
                // Worker is shutting down; always requeue regardless of retry budget
                // because this is an infrastructure event, not an execution failure.
                // Use CancellationToken.None so store/queue cleanup can complete
                // after the host stopping signal has fired.
                await AbandonJobAsync(running, workerId, "Worker shutdown.",
                    CancellationToken.None, forceRequeue: true).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            await StopHeartbeatPumpAsync().ConfigureAwait(false);
            activity?.SetStatus(ActivityStatusCode.Error, "Execution timed out.");
            Log.JobTimedOut(logger, operationId, timeoutPolicy.MaxDuration);
            // Timeout — terminal failure, do not retry.
            await TerminateJobAsync(operationId, workerId, ExecutionJobStatus.Failed,
                $"Execution timed out after {timeoutPolicy.MaxDuration}.", CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (partitionLeaseLostCts.IsCancellationRequested)
        {
            await StopHeartbeatPumpAsync().ConfigureAwait(false);
            activity?.SetStatus(ActivityStatusCode.Error, "Exclusive partition lease lost.");
            await AbandonJobAsync(
                    running,
                    workerId,
                    "Exclusive partition lease was lost during execution.",
                    CancellationToken.None,
                    forceRequeue: true,
                    requeueDelayOverride: _partitionLeaseContentionDelay)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await StopHeartbeatPumpAsync().ConfigureAwait(false);
            activity?.SetStatus(ActivityStatusCode.Error, "Cancelled by operator.");
            Log.JobCancelledByOperator(logger, operationId);
            // Operator cancellation — terminal cancelled state.
            await TerminateJobAsync(operationId, workerId, ExecutionJobStatus.Cancelled,
                "Cancelled by operator.", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Deliberately broad: any unhandled executor exception must be caught here so
            // one job's failure routes through the retry/abandon policy instead of
            // crashing the worker's claim loop.
            await StopHeartbeatPumpAsync().ConfigureAwait(false);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            Log.JobExecutionFailed(logger, operationId, ex);
            // Execution exception — route through retry policy.
            await AbandonJobAsync(running, workerId, SafeExecutionFailureMessage, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await partitionLeaseRenewalCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await partitionLeaseRenewalTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when execution completes before the next renewal interval.
            }

            if (partitionLeaseId is not null)
            {
                try
                {
                    await jobStore.ReleaseLeaseAsync(
                            partitionLeaseId,
                            partitionLeaseOwner!,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // The lease is bounded by _partitionLeaseDuration, so a failed best-effort
                    // release cannot strand the partition indefinitely.
                    Log.PartitionLeaseReleaseFailed(logger, operationId, partitionKey!, ex);
                }
            }

            cancellationTokens.Remove(operationId, workerId);
        }
    }

    private async Task RenewPartitionLeaseUntilCancelledAsync(
        string operationId,
        string partitionKey,
        string partitionLeaseId,
        string partitionLeaseOwner,
        CancellationTokenSource partitionLeaseLostCts,
        CancellationTokenSource jobCts,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_partitionLeaseRenewInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            bool renewed;
            try
            {
                renewed = await jobStore.RenewLeaseAsync(
                        partitionLeaseId,
                        partitionLeaseOwner,
                        _partitionLeaseDuration,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.PartitionLeaseRenewalFailed(logger, operationId, partitionKey, ex);
                renewed = false;
            }

            if (renewed)
            {
                continue;
            }

            Log.PartitionLeaseLost(logger, operationId, partitionKey);
            await partitionLeaseLostCts.CancelAsync().ConfigureAwait(false);
            await jobCts.CancelAsync().ConfigureAwait(false);
            return;
        }
    }

    private async Task<bool> VerifyPartitionLeaseOwnershipAsync(
        string operationId,
        string? partitionKey,
        string? partitionLeaseId,
        string? partitionLeaseOwner)
    {
        if (partitionLeaseId is null)
        {
            return true;
        }

        try
        {
            // Redis LockExtend is an atomic owner comparison and renewal. Performing it after the
            // executor returns closes the interval before terminal finalization in which a paused
            // worker's lease can expire before the background renewal observes the loss.
            return await jobStore.RenewLeaseAsync(
                    partitionLeaseId,
                    partitionLeaseOwner!,
                    _partitionLeaseDuration,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.PartitionLeaseRenewalFailed(logger, operationId, partitionKey!, ex);
            return false;
        }
    }

    private static string BuildPartitionLeaseId(string partitionKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(partitionKey));
        return $"partition:{Convert.ToHexString(hash)}";
    }

    private async Task<bool> FinalizeJobAsync(
        string operationId,
        string workerId,
        JobExecutionResult result,
        string? partitionLeaseId,
        string? partitionLeaseOwner,
        CancellationToken cancellationToken)
    {
        var job = await jobStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (job == null)
        {
            return true;
        }

        if (IsTerminalOrNotOwnedBy(job, workerId))
        {
            Log.TerminalStateSkipped(logger, operationId, job.Status.ToString());
            return true;
        }

        // Durable cancellation wins over a racing success (#3089): the artifact
        // publication fence already refuses to publish once CancellationRequestedAt
        // is stamped, so finalizing this record as Succeeded would durably expose a
        // success with silently missing outputs and no repair path. Honour the stamp
        // and finalize as Cancelled instead — consistent with every other path that
        // observes the durable signal.
        var effectiveStatus = result.Status;
        if (result.Status == ExecutionJobStatus.Succeeded && job.CancellationRequestedAt.HasValue)
        {
            Log.FinalizeHonouredDurableCancellation(logger, operationId);
            effectiveStatus = ExecutionJobStatus.Cancelled;
        }

        var now = DateTimeOffset.UtcNow;
        var final = job with
        {
            Status = effectiveStatus,
            UpdatedAt = now,
            CompletedAt = now,
            ErrorMessage = effectiveStatus switch
            {
                ExecutionJobStatus.Failed => SafeExecutionFailureMessage,
                ExecutionJobStatus.Cancelled => "Cancelled by operator (durable signal honoured at finalization).",
                _ => null
            },
            Warnings = result.Warnings,
            PercentComplete = effectiveStatus == ExecutionJobStatus.Succeeded ? 100 : job.PercentComplete,
            CurrentPhase = effectiveStatus switch
            {
                ExecutionJobStatus.Succeeded => "Completed",
                ExecutionJobStatus.Cancelled => "Cancelled",
                _ => "Failed"
            }
        };

        cancellationToken.ThrowIfCancellationRequested();
        var finalized = partitionLeaseId is null
            ? await jobStore.TrySetAsync(final, cancellationToken: cancellationToken).ConfigureAwait(false)
            : await jobStore.TrySetIfLeaseOwnedAsync(
                    final,
                    partitionLeaseId,
                    partitionLeaseOwner!,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        if (!finalized)
        {
            Log.TerminalStateSkipped(logger, operationId, "CAS conflict or partition lease lost on finalize");
            return false;
        }

        // Expiry may race a store that commits before observing its cancellation token.
        // The caller's expiry handler replaces that attempt's late success with failure.
        cancellationToken.ThrowIfCancellationRequested();

        ControlPlaneTelemetry.RecordExecutionTransition(job, final);

        cancellationTokens.Remove(operationId, workerId);

        // Both catches below are deliberately broad: the durable job record already
        // transitioned to its terminal state above, so a queue/log-store failure here
        // is best-effort cleanup that must not fail finalization — the stale-claim
        // reconciler and log TTL cover the recovery path.
        try
        {
            await jobQueue.RemoveAsync(operationId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.QueueRemovalFailed(logger, operationId, ex);
        }

        if (logStore != null)
        {
            try
            {
                await logStore.SetRetentionAsync(operationId, LogRetention, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.LogRetentionFailed(logger, operationId, ex);
            }
        }

        await NotifyTerminalAsync(final, cancellationToken).ConfigureAwait(false);
        Log.JobExecutionCompleted(logger, operationId, effectiveStatus.ToString());
        return true;
    }

    private async Task TerminateJobAsync(
        string operationId,
        string workerId,
        ExecutionJobStatus terminalStatus,
        string reason,
        CancellationToken cancellationToken)
    {
        var job = await jobStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (job == null)
        {
            return;
        }

        var lateExpiredSuccess = reason == "license expired" && job.Status == ExecutionJobStatus.Succeeded &&
            string.Equals(job.ClaimedBy, workerId, StringComparison.Ordinal);
        if (IsTerminalOrNotOwnedBy(job, workerId) && !lateExpiredSuccess)
        {
            Log.TerminalStateSkipped(logger, operationId, job.Status.ToString());
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var terminal = job with
        {
            Status = terminalStatus,
            UpdatedAt = now,
            CompletedAt = now,
            ErrorMessage = reason,
            ArtifactReferences = reason == "license expired" ? [] : job.ArtifactReferences,
            PercentComplete = reason == "license expired" ? null : job.PercentComplete,
            CurrentPhase = terminalStatus == ExecutionJobStatus.Cancelled ? "Cancelled" : "Failed"
        };

        if (!await jobStore.TrySetAsync(terminal, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            Log.TerminalStateSkipped(logger, operationId, "CAS conflict on terminate");
            cancellationTokens.Remove(operationId, workerId);
            return;
        }

        ControlPlaneTelemetry.RecordExecutionTransition(job, terminal);

        cancellationTokens.Remove(operationId, workerId);

        // Both catches below are deliberately broad — see FinalizeJobAsync above for the
        // same best-effort cleanup rationale.
        try
        {
            await jobQueue.RemoveAsync(operationId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.QueueRemovalFailed(logger, operationId, ex);
        }

        if (logStore != null)
        {
            try
            {
                await logStore.SetRetentionAsync(operationId, LogRetention, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.LogRetentionFailed(logger, operationId, ex);
            }
        }

        await NotifyTerminalAsync(terminal, cancellationToken).ConfigureAwait(false);
        Log.JobExecutionCompleted(logger, operationId, terminalStatus.ToString());
    }

    private async Task AbandonJobAsync(
        ExecutionJobRecord job,
        string workerId,
        string reason,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? warnings = null,
        bool forceRequeue = false,
        TimeSpan? requeueDelayOverride = null,
        bool restoreClaimAttempt = false)
    {
        // Re-read to capture progress and artifact updates made during execution.
        var current = await jobStore.GetAsync(job.OperationId, cancellationToken).ConfigureAwait(false) ?? job;

        if (IsTerminalOrNotOwnedBy(current, workerId))
        {
            Log.TerminalStateSkipped(logger, current.OperationId, current.Status.ToString());
            return;
        }

        if (current.CancellationRequestedAt.HasValue)
        {
            Log.AbandonHonouredDurableCancellation(logger, current.OperationId);

            var cancelNow = DateTimeOffset.UtcNow;
            var cancelled = current with
            {
                Status = ExecutionJobStatus.Cancelled,
                UpdatedAt = cancelNow,
                CompletedAt = cancelNow,
                ErrorMessage = "Cancelled by operator (durable signal honoured during abandon).",
                CurrentPhase = "Cancelled"
            };
            if (!await jobStore.TrySetAsync(cancelled, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                cancellationTokens.Remove(current.OperationId, workerId);
                return;
            }

            ControlPlaneTelemetry.RecordExecutionTransition(current, cancelled);

            cancellationTokens.Remove(current.OperationId, workerId);

            // Both catches below are deliberately broad — see FinalizeJobAsync above for the
            // same best-effort cleanup rationale.
            try
            {
                await jobQueue.RemoveAsync(current.OperationId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.QueueRemovalFailed(logger, current.OperationId, ex);
            }

            if (logStore != null)
            {
                try
                {
                    await logStore.SetRetentionAsync(current.OperationId, LogRetention, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Log.LogRetentionFailed(logger, current.OperationId, ex);
                }
            }

            await NotifyTerminalAsync(cancelled, cancellationToken).ConfigureAwait(false);
            return;
        }

        var retryPolicy = current.RetryPolicy ?? JobRetryPolicy.Default;

        if (forceRequeue || retryPolicy.ShouldRetry(current.AttemptCount))
        {
            Log.JobAbandoned(logger, current.OperationId, reason);

            // Persist per-attempt warnings to structured execution logs before
            // clearing them from the requeued record, so they remain observable.
            // Best-effort: a transient log-store failure must not block the
            // durable requeue/terminal transition.
            if (warnings is { Count: > 0 } && logStore != null)
            {
                try
                {
                    foreach (var warning in warnings)
                    {
                        await logStore.AppendAsync(current.OperationId, new ExecutionLogEntry
                        {
                            Timestamp = DateTimeOffset.UtcNow,
                            Level = ExecutionLogLevel.Warning,
                            Message = warning,
                            Phase = $"Attempt {current.AttemptCount} (requeuing)"
                        }, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Log.WarningLogAppendFailed(logger, current.OperationId, ex);
                }
            }

            // Re-read before the requeue write to catch cancellation signals
            // that arrived during the log-append phase.
            var latestBeforeRequeue = await jobStore.GetAsync(current.OperationId, cancellationToken).ConfigureAwait(false);
            if (latestBeforeRequeue == null || IsTerminalOrNotOwnedBy(latestBeforeRequeue, workerId))
            {
                cancellationTokens.Remove(current.OperationId, workerId);
                return;
            }

            if (latestBeforeRequeue.CancellationRequestedAt.HasValue)
            {
                Log.AbandonHonouredDurableCancellation(logger, current.OperationId);
                await TerminateJobAsync(current.OperationId, workerId, ExecutionJobStatus.Cancelled,
                    "Cancelled by operator (durable signal honoured during abandon).", cancellationToken).ConfigureAwait(false);
                return;
            }

            var delay = requeueDelayOverride
                ?? (forceRequeue ? TimeSpan.Zero : retryPolicy.ComputeDelay(latestBeforeRequeue.AttemptCount + 1));
            var now = DateTimeOffset.UtcNow;
            var abandoned = latestBeforeRequeue with
            {
                Status = ExecutionJobStatus.Queued,
                ClaimedBy = null,
                ClaimedAt = null,
                LastHeartbeatAt = null,
                UpdatedAt = now,
                CurrentPhase = $"Requeued: {reason}",
                PercentComplete = null,
                ErrorMessage = null,
                ProviderOperationId = null,
                CompletedAt = null,
                ArtifactReferences = Array.Empty<string>(),
                Warnings = Array.Empty<string>(),
                AttemptCount = restoreClaimAttempt
                    ? Math.Max(0, latestBeforeRequeue.AttemptCount - 1)
                    : latestBeforeRequeue.AttemptCount,
                NextRetryAt = delay > TimeSpan.Zero ? now.Add(delay) : null
            };
            if (!await jobStore.TrySetAsync(abandoned, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                cancellationTokens.Remove(latestBeforeRequeue.OperationId, workerId);
                return;
            }

            ControlPlaneTelemetry.RecordExecutionTransition(latestBeforeRequeue, abandoned);

            // Clear the tracked CTS immediately after the authoritative store
            // transition to Queued, before the queue write. If RequeueAsync
            // fails, cancel paths will not delegate to the dead worker and
            // the stale-claim reconciler will repair the queue state.
            cancellationTokens.Remove(latestBeforeRequeue.OperationId, workerId);

            await jobQueue.RequeueAsync(
                latestBeforeRequeue.OperationId,
                latestBeforeRequeue.Priority,
                delay > TimeSpan.Zero ? delay : null,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Re-read before the fail write to catch late cancellation signals.
            var latestBeforeFail = await jobStore.GetAsync(current.OperationId, cancellationToken).ConfigureAwait(false);
            if (latestBeforeFail == null || IsTerminalOrNotOwnedBy(latestBeforeFail, workerId))
            {
                cancellationTokens.Remove(current.OperationId, workerId);
                return;
            }

            if (latestBeforeFail.CancellationRequestedAt.HasValue)
            {
                Log.AbandonHonouredDurableCancellation(logger, current.OperationId);
                await TerminateJobAsync(current.OperationId, workerId, ExecutionJobStatus.Cancelled,
                    "Cancelled by operator (durable signal honoured during abandon).", cancellationToken).ConfigureAwait(false);
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var failed = latestBeforeFail with
            {
                Status = ExecutionJobStatus.Failed,
                UpdatedAt = now,
                CompletedAt = now,
                // Persist the caller-supplied reason: a curated, client-safe
                // executor failure detail (#2348) or the generic safe message for
                // unexpected thrown exceptions. Never a raw exception string.
                ErrorMessage = reason,
                Warnings = warnings ?? latestBeforeFail.Warnings,
                CurrentPhase = "Failed (abandoned)"
            };
            if (!await jobStore.TrySetAsync(failed, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                cancellationTokens.Remove(latestBeforeFail.OperationId, workerId);
                return;
            }

            ControlPlaneTelemetry.RecordExecutionTransition(latestBeforeFail, failed);

            cancellationTokens.Remove(latestBeforeFail.OperationId, workerId);

            // Both catches below are deliberately broad — see FinalizeJobAsync above for the
            // same best-effort cleanup rationale.
            try
            {
                await jobQueue.RemoveAsync(latestBeforeFail.OperationId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.QueueRemovalFailed(logger, latestBeforeFail.OperationId, ex);
            }

            if (logStore != null)
            {
                try
                {
                    await logStore.SetRetentionAsync(latestBeforeFail.OperationId, LogRetention, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Log.LogRetentionFailed(logger, latestBeforeFail.OperationId, ex);
                }
            }

            await NotifyTerminalAsync(failed, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task NotifyTerminalAsync(ExecutionJobRecord job, CancellationToken cancellationToken)
    {
        foreach (var callback in terminalCallbacks)
        {
            try
            {
                await callback.OnTerminalAsync(job, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Deliberately broad: one misbehaving terminal callback must not stop the
                // remaining callbacks from running, and the job's own terminal transition
                // already persisted before this notification fan-out.
                Log.TerminalCallbackFailed(logger, job.OperationId, ex);
            }
        }
    }

    /// <summary>
    /// Returns <c>true</c> when the job record is no longer in an active state owned
    /// by <paramref name="workerId"/>, indicating that the reconciler or another
    /// worker has already transitioned the job. Callers should skip their own
    /// state transition to avoid overwriting the authoritative update.
    /// </summary>
    private static bool IsTerminalOrNotOwnedBy(ExecutionJobRecord job, string workerId)
        => job.Status is not (ExecutionJobStatus.Provisioning or ExecutionJobStatus.Running)
           || job.ClaimedBy != workerId;

    private static bool IsTerminal(ExecutionJobStatus status)
        => status is ExecutionJobStatus.Succeeded
            or ExecutionJobStatus.Failed
            or ExecutionJobStatus.Cancelled;

    /// <summary>
    /// Builds a worker ID that always preserves the full GUID suffix for ownership
    /// uniqueness, truncating the machine-name prefix when necessary to stay within
    /// the 48-character budget.
    /// </summary>
    internal static string GenerateWorkerId()
        => GenerateWorkerId(Environment.MachineName, Guid.NewGuid());

    internal static string GenerateWorkerId(string machineName, Guid workerGuid)
    {
        var guid = workerGuid.ToString("N");
        var safeMachineName = string.IsNullOrWhiteSpace(machineName) ? "unknown" : machineName;
        var prefix = $"worker-{safeMachineName}";
        const int maxLength = 48;
        const int separatorLength = 1;
        var maxPrefixLength = maxLength - guid.Length - separatorLength;
        if (prefix.Length > maxPrefixLength)
        {
            prefix = prefix[..maxPrefixLength];
        }

        return $"{prefix}-{guid}";
    }

    private static partial class Log
    {
        [LoggerMessage(9050, LogLevel.Information, "Job execution worker started: {WorkerId}")]
        public static partial void WorkerStarted(ILogger logger, string workerId);

        [LoggerMessage(9051, LogLevel.Information, "Job execution worker stopped: {WorkerId}")]
        public static partial void WorkerStopped(ILogger logger, string workerId);

        [LoggerMessage(9052, LogLevel.Information, "Job execution started: {OperationId}, Kind={Kind}")]
        public static partial void JobExecutionStarted(ILogger logger, string operationId, string kind);

        [LoggerMessage(9053, LogLevel.Information, "Job execution completed: {OperationId}, Status={Status}")]
        public static partial void JobExecutionCompleted(ILogger logger, string operationId, string status);

        [LoggerMessage(9054, LogLevel.Error, "Job execution failed: {OperationId}")]
        public static partial void JobExecutionFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9055, LogLevel.Warning, "Job not found during execution: {OperationId}")]
        public static partial void JobNotFoundDuringExecution(ILogger logger, string operationId);

        [LoggerMessage(9056, LogLevel.Warning, "No executor registered for job kind: {OperationId}, Kind={Kind}")]
        public static partial void NoExecutorForKind(ILogger logger, string operationId, string kind);

        [LoggerMessage(9057, LogLevel.Warning, "Job abandoned: {OperationId}, Reason={Reason}")]
        public static partial void JobAbandoned(ILogger logger, string operationId, string reason);

        [LoggerMessage(9058, LogLevel.Error, "Claim loop error")]
        public static partial void ClaimLoopError(ILogger logger, Exception exception);

        [LoggerMessage(9059, LogLevel.Warning, "Job timed out: {OperationId}, MaxDuration={MaxDuration}")]
        public static partial void JobTimedOut(ILogger logger, string operationId, TimeSpan maxDuration);

        [LoggerMessage(9060, LogLevel.Information, "Job cancelled by operator: {OperationId}")]
        public static partial void JobCancelledByOperator(ILogger logger, string operationId);

        [LoggerMessage(9061, LogLevel.Warning, "No job executors registered for worker {WorkerId}; claim loop will not match any jobs")]
        public static partial void NoExecutorsRegistered(ILogger logger, string workerId);

        [LoggerMessage(9062, LogLevel.Warning, "Skipping state transition for job {OperationId}: current status is {Status} (reconciler or another worker intervened)")]
        public static partial void TerminalStateSkipped(ILogger logger, string operationId, string status);

        [LoggerMessage(9063, LogLevel.Warning, "Heartbeat pump faulted for job {OperationId}; finalization will proceed from executor outcome")]
        public static partial void HeartbeatPumpFaulted(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9065, LogLevel.Warning, "Failed to persist per-attempt warnings for job {OperationId}; requeue/terminal transition will proceed")]
        public static partial void WarningLogAppendFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9066, LogLevel.Error, "Failed to requeue job {OperationId} during pre-execution shutdown; stale-claim reconciliation will recover")]
        public static partial void PreExecShutdownCleanupFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9068, LogLevel.Information, "Abandon honoured durable cancellation signal for job {OperationId}")]
        public static partial void AbandonHonouredDurableCancellation(ILogger logger, string operationId);

        [LoggerMessage(9069, LogLevel.Warning, "Terminal callback failed for job {OperationId}; admin progress may be stale")]
        public static partial void TerminalCallbackFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9075, LogLevel.Information, "Job {OperationId} is waiting for exclusive partition lease {PartitionKey}")]
        public static partial void PartitionLeaseUnavailable(ILogger logger, string operationId, string partitionKey);

        [LoggerMessage(9076, LogLevel.Warning, "Job {OperationId} lost exclusive partition lease {PartitionKey}; cancelling execution")]
        public static partial void PartitionLeaseLost(ILogger logger, string operationId, string partitionKey);

        [LoggerMessage(9077, LogLevel.Warning, "Failed to renew exclusive partition lease {PartitionKey} for job {OperationId}")]
        public static partial void PartitionLeaseRenewalFailed(
            ILogger logger,
            string operationId,
            string partitionKey,
            Exception exception);

        [LoggerMessage(9078, LogLevel.Warning, "Failed to release exclusive partition lease {PartitionKey} for job {OperationId}")]
        public static partial void PartitionLeaseReleaseFailed(
            ILogger logger,
            string operationId,
            string partitionKey,
            Exception exception);

        [LoggerMessage(9079, LogLevel.Warning, "Failed to acquire exclusive partition lease {PartitionKey} for job {OperationId}")]
        public static partial void PartitionLeaseAcquisitionFailed(
            ILogger logger,
            string operationId,
            string partitionKey,
            Exception exception);

        [LoggerMessage(9071, LogLevel.Warning, "Queue removal failed for terminal job {OperationId}; stale-claim reconciler will repair")]
        public static partial void QueueRemovalFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9073, LogLevel.Warning, "Log retention set failed for terminal job {OperationId}; execution logs may expire early")]
        public static partial void LogRetentionFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9074, LogLevel.Warning, "Job executor returned failure: {OperationId}, Error={ErrorMessage}")]
        public static partial void JobExecutorReturnedFailure(ILogger logger, string operationId, string errorMessage);

        [LoggerMessage(9077, LogLevel.Information, "Finalize honoured durable cancellation signal for job {OperationId}: success result finalized as Cancelled")]
        public static partial void FinalizeHonouredDurableCancellation(ILogger logger, string operationId);
    }
}

/// <summary>
/// Execution context provided to <see cref="IJobExecutor"/> during job execution.
/// Mediates heartbeat pumping, progress reporting, log appending, and artifact publication.
/// Serializes read-modify-write operations on the job record to prevent concurrent
/// heartbeat, progress, and artifact writes from clobbering each other.
/// <paramref name="claimedAttempt"/> carries the attempt number observed when the
/// worker claimed the job; artifact publication is fenced on it so a stale attempt
/// (a resurrected worker whose job was requeued and reclaimed) can never publish
/// into a newer attempt's record (#3089, ADR-0071). Zero disables only the attempt
/// comparison (test harness contexts); ownership, cancellation, and version fences
/// always apply.
/// </summary>
internal sealed partial class JobExecutionContext(
    string operationId,
    string workerId,
    IExecutionJobStore jobStore,
    IExecutionLogStore? logStore,
    JobHeartbeatPolicy heartbeatPolicy,
    CancellationTokenSource? durableCancellationCts,
    ILogger logger,
    int claimedAttempt = 0,
    CancellationToken licenseCancellation = default) : IJobExecutionContext, IDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public string OperationId => operationId;

    public async Task ReportProgressAsync(
        double? percentComplete,
        string? phase,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var job = await jobStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (job == null || !IsOwnedBy(job))
            {
                return;
            }

            var updated = job with
            {
                PercentComplete = percentComplete,
                CurrentPhase = phase ?? job.CurrentPhase,
                UpdatedAt = DateTimeOffset.UtcNow,
                LastHeartbeatAt = DateTimeOffset.UtcNow
            };
            await jobStore.TrySetAsync(updated, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task AppendLogAsync(
        ExecutionLogEntry entry,
        CancellationToken cancellationToken = default)
    {
        if (logStore == null)
        {
            return;
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var job = await jobStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (job == null || !IsOwnedBy(job))
            {
                return;
            }

            await logStore.AppendAsync(operationId, entry, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Durably appends an artifact reference under the publication fence (#3089):
    /// the write is rejected when the job is no longer owned by this worker, when the
    /// record's attempt no longer matches the claimed attempt (a retried attempt owns
    /// the record now), or when durable cancellation was requested. Publication is
    /// idempotent for typed raster output descriptors — republishing a descriptor for
    /// an output name this attempt already published cannot produce a duplicate entry.
    /// Legacy references keep append semantics (distinct outputs may be byte-identical).
    /// Version conflicts are re-read and retried instead of being silently dropped.
    /// </summary>
    public async Task PublishArtifactAsync(
        string artifactReference,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactReference);
        const int maxCasRetries = 10;

        licenseCancellation.ThrowIfCancellationRequested();

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; attempt < maxCasRetries; attempt++)
            {
                licenseCancellation.ThrowIfCancellationRequested();
                var job = await jobStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
                if (job == null || !IsOwnedBy(job))
                {
                    return;
                }

                if (claimedAttempt > 0 && job.AttemptCount != claimedAttempt)
                {
                    // A stale attempt must never publish into a newer attempt's record.
                    Log.ArtifactPublishFencedStaleAttempt(logger, operationId, claimedAttempt, job.AttemptCount);
                    return;
                }

                if (job.CancellationRequestedAt.HasValue)
                {
                    // Durable cancellation wins: an attempt racing its own cancellation
                    // cannot expose new output through the job record.
                    Log.ArtifactPublishFencedCancellation(logger, operationId);
                    return;
                }

                if (!TryAppendArtifactReference(job.ArtifactReferences, artifactReference, out var refs))
                {
                    // Identical publication already durable — retried publish is a no-op.
                    return;
                }

                var updated = job with
                {
                    ArtifactReferences = refs,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    LastHeartbeatAt = DateTimeOffset.UtcNow
                };
                if (await jobStore.TrySetAsync(updated, cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                // Version conflict (heartbeat/reconciler write raced this publish):
                // re-read and re-evaluate the fence rather than dropping the artifact.
            }

            throw new InvalidOperationException(
                $"Failed to durably publish an artifact reference for job '{operationId}' after {maxCasRetries} "
                + "version conflicts.");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task ThrowIfExecutionLeaseLostAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var job = await jobStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (job == null || !IsOwnedBy(job)
                || (claimedAttempt > 0 && job.AttemptCount != claimedAttempt)
                || job.CancellationRequestedAt.HasValue)
            {
                throw new InvalidOperationException(
                    $"Execution lease for job '{operationId}' is no longer owned by worker '{workerId}'.");
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Builds the updated reference list, returning <see langword="false"/> when the
    /// publication is already durably present. Only typed raster output descriptors
    /// dedupe — they carry an (attempt, output name) identity, so republishing the same
    /// logical output within an attempt replaces its entry instead of appending a
    /// duplicate. Legacy references (data URIs, provider links) keep strict append
    /// semantics: two distinct outputs may legitimately have byte-identical payloads,
    /// and collapsing them would shift the positional slot/kind mapping downstream.
    /// </summary>
    private static bool TryAppendArtifactReference(
        IReadOnlyList<string> existing,
        string artifactReference,
        out List<string> updated)
    {
        updated = new List<string>(existing.Count + 1);

        if (Honua.Core.Features.Geoprocessing.Raster.RasterOutputJson.TryDeserialize(
                artifactReference, out var incoming))
        {
            var replaced = false;
            foreach (var entry in existing)
            {
                if (!replaced
                    && Honua.Core.Features.Geoprocessing.Raster.RasterOutputJson.TryDeserialize(entry, out var current)
                    && current.AttemptNumber == incoming.AttemptNumber
                    && string.Equals(current.OutputName, incoming.OutputName, StringComparison.Ordinal))
                {
                    if (string.Equals(entry, artifactReference, StringComparison.Ordinal))
                    {
                        return false;
                    }

                    updated.Add(artifactReference);
                    replaced = true;
                    continue;
                }

                updated.Add(entry);
            }

            if (!replaced)
            {
                updated.Add(artifactReference);
            }

            return true;
        }

        updated.AddRange(existing);
        updated.Add(artifactReference);
        return true;
    }

    /// <summary>
    /// Pumps heartbeat signals at the configured interval until cancelled.
    /// </summary>
    internal async Task RunHeartbeatPumpAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(heartbeatPolicy.Interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var job = await jobStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
                    if (job == null)
                    {
                        break;
                    }

                    // Stop pumping if the reconciler already marked this job terminal
                    // or ownership has moved to another worker.
                    if (!IsOwnedBy(job))
                    {
                        break;
                    }

                    // A remote API host persisted a durable cancellation signal.
                    // Cancel the local CTS so the executor receives the signal
                    // through its CancellationToken.
                    if (job.CancellationRequestedAt.HasValue && durableCancellationCts != null)
                    {
                        try { durableCancellationCts.Cancel(); }
                        catch (ObjectDisposedException)
                        {
                            // Expected race: the execution task completed and disposed
                            // the CTS between the read above and this cancel. Benign.
                        }
                        break;
                    }

                    var updated = job with
                    {
                        LastHeartbeatAt = DateTimeOffset.UtcNow,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    await jobStore.TrySetAsync(updated, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _writeLock.Release();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Deliberately broad: a single failed heartbeat write must not stop the
                // pump loop; log and retry on the next interval.
                Log.HeartbeatWriteFailed(logger, operationId, ex);
            }
        }
    }

    private bool IsOwnedBy(ExecutionJobRecord job)
        => job.Status is ExecutionJobStatus.Provisioning or ExecutionJobStatus.Running
           && job.ClaimedBy == workerId;

    private static partial class Log
    {
        [LoggerMessage(9064, LogLevel.Warning, "Heartbeat write failed for job {OperationId}; pump will retry on next interval")]
        public static partial void HeartbeatWriteFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9075, LogLevel.Warning, "Artifact publish fenced for job {OperationId}: claimed attempt {ClaimedAttempt} is stale (record attempt {RecordAttempt})")]
        public static partial void ArtifactPublishFencedStaleAttempt(ILogger logger, string operationId, int claimedAttempt, int recordAttempt);

        [LoggerMessage(9076, LogLevel.Information, "Artifact publish fenced for job {OperationId}: durable cancellation requested")]
        public static partial void ArtifactPublishFencedCancellation(ILogger logger, string operationId);
    }

    public void Dispose() => _writeLock.Dispose();
}
