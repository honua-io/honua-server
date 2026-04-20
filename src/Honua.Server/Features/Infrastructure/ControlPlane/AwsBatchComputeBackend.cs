// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using Amazon.Runtime;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Features.Infrastructure.ControlPlane;

/// <summary>
/// Parameter keys used on <see cref="ExecutionJobSpec.Parameters"/> to drive AWS Batch submissions.
/// </summary>
internal static class AwsBatchParameterKeys
{
    public const string JobDefinitionArn = "batch.job_definition_arn";
    public const string JobQueueArn = "batch.job_queue_arn";
    public const string Region = "batch.region";
    public const string TimeoutSeconds = "batch.timeout_seconds";
    public const string Vcpus = "batch.vcpus";
    public const string MemoryMib = "batch.memory_mib";
    public const string GpuCount = "batch.gpu_count";
    public const string RetryAttempts = "batch.retry_attempts";
    public const string ShareIdentifier = "batch.share_identifier";
}

/// <summary>
/// Pure mapping from AWS Batch job status strings to <see cref="ExecutionJobStatus"/>.
/// </summary>
internal static class AwsBatchStateMapper
{
    /// <summary>
    /// Reason string attached to AWS Batch cancel and terminate requests. The reason surfaces
    /// back via DescribeJobs.StatusReason once AWS reaches FAILED, letting the adapter distinguish
    /// operator-initiated cancellation from real workload failures.
    /// </summary>
    public const string CancelReason = "Cancelled by Honua control plane";

    public static ExecutionJobStatus MapStatus(string? awsStatus)
    {
        if (string.IsNullOrWhiteSpace(awsStatus))
        {
            return ExecutionJobStatus.Running;
        }

        return awsStatus.Trim().ToUpperInvariant() switch
        {
            "SUBMITTED" => ExecutionJobStatus.Queued,
            "PENDING" => ExecutionJobStatus.Queued,
            "RUNNABLE" => ExecutionJobStatus.Queued,
            "STARTING" => ExecutionJobStatus.Provisioning,
            "RUNNING" => ExecutionJobStatus.Running,
            "SUCCEEDED" => ExecutionJobStatus.Succeeded,
            "FAILED" => ExecutionJobStatus.Failed,
            _ => ExecutionJobStatus.Running
        };
    }

    /// <summary>
    /// Maps AWS Batch status, promoting FAILED to Cancelled when the provider's statusReason
    /// indicates the failure was caused by a cancel/terminate request we issued.
    /// </summary>
    public static ExecutionJobStatus MapStatusWithReason(string? awsStatus, string? statusReason)
    {
        var mapped = MapStatus(awsStatus);
        if (mapped == ExecutionJobStatus.Failed && MatchesCancelReason(statusReason))
        {
            return ExecutionJobStatus.Cancelled;
        }

        return mapped;
    }

    public static bool MatchesCancelReason(string? statusReason)
        => !string.IsNullOrEmpty(statusReason)
            && statusReason.Contains(CancelReason, StringComparison.Ordinal);

    public static bool IsTerminal(ExecutionJobStatus status)
        => status is ExecutionJobStatus.Succeeded
            or ExecutionJobStatus.Failed
            or ExecutionJobStatus.Cancelled;

    public static bool IsInFlight(string? awsStatus)
    {
        var mapped = MapStatus(awsStatus);
        return !IsTerminal(mapped);
    }

    public static bool CanCancelWithoutTerminate(string? awsStatus)
    {
        if (string.IsNullOrWhiteSpace(awsStatus))
        {
            return false;
        }

        var normalized = awsStatus.Trim().ToUpperInvariant();
        return normalized is "SUBMITTED" or "PENDING" or "RUNNABLE";
    }
}

/// <summary>
/// AWS Batch execution adapter behind the canonical batch-compute boundary.
/// </summary>
internal sealed partial class AwsBatchComputeBackend(
    IAwsBatchJobClient batchClient,
    ILogger<AwsBatchComputeBackend> logger) : IBatchComputeBackend
{
    internal const string AdapterBackendName = "honua-aws-batch";

    /// <summary>
    /// Sentinel prefix stored in <see cref="ExecutionJobRecord.ProviderOperationId"/> when a
    /// SubmitJob call failed with a transport-ambiguous error. The remainder is the deterministic
    /// AWS Batch job name, which reconciliation resolves to a concrete provider job id via
    /// <see cref="IAwsBatchJobClient.ListJobsByNameAsync"/>. Using a sentinel keeps the record
    /// nonterminal (so the reconciler takes the Observe path) without pretending we have a real
    /// provider id.
    /// </summary>
    internal const string PendingSubmissionMarkerPrefix = "aws-batch-pending:";

    /// <summary>
    /// Bounded grace window the reconciler allows for an uncertain submit before transitioning
    /// to a terminal state. Mirrors <see cref="AzureBatchComputeBackend.MissingRegistrationGracePeriod"/>
    /// so operators get the same "provider never acknowledged submission" semantics across backends.
    /// </summary>
    internal static TimeSpan PendingDiscoveryGracePeriod => TimeSpan.FromMinutes(2);

    private static readonly BatchComputeBackendCapabilities CapabilitiesSnapshot = new()
    {
        SupportsCancellation = true,
        SupportsProgressPolling = true,
        SupportsRetry = true,
        SupportsLogStreaming = false,
        SupportsArtifactStaging = false
    };

    internal static bool TryExtractPendingJobName(string? providerOperationId, out string jobName)
    {
        if (!string.IsNullOrWhiteSpace(providerOperationId)
            && providerOperationId.StartsWith(PendingSubmissionMarkerPrefix, StringComparison.Ordinal))
        {
            jobName = providerOperationId[PendingSubmissionMarkerPrefix.Length..];
            return !string.IsNullOrWhiteSpace(jobName);
        }

        jobName = string.Empty;
        return false;
    }

    /// <summary>
    /// True when the durable record has already crossed the remote-start boundary but is
    /// missing a concrete provider id. Happens when a crash or CAS conflict lost the
    /// post-start write in <see cref="ExecutionJobSubmissionHelper.StartOnRemoteBackendAsync"/>
    /// after AWS Batch accepted (or may have accepted) the submission. Without this
    /// recovery path, observe/cancel would short-circuit to "not submitted yet" and orphan
    /// the real provider job. The per-attempt job name is deterministic, so reconciliation
    /// can rediscover the job via ListJobsByName.
    /// </summary>
    internal static bool TryDeriveOrphanedSubmissionName(ExecutionJobRecord job, out string jobName)
    {
        if (!string.IsNullOrWhiteSpace(job.ProviderOperationId))
        {
            jobName = string.Empty;
            return false;
        }

        // The initial pre-submission state is Status=Queued with AttemptCount=0. Anything
        // else means StartAsync ran at least once: Status was advanced to Provisioning
        // before the first call, or AttemptCount was incremented after a prior success.
        if (job.Status == ExecutionJobStatus.Queued && job.AttemptCount == 0)
        {
            jobName = string.Empty;
            return false;
        }

        jobName = BuildJobName(job.OperationId, job.AttemptCount);
        return true;
    }

    /// <summary>
    /// True when the exception is part of the AWS SDK runtime exception family
    /// (<see cref="AmazonServiceException"/> for service-level failures or
    /// <see cref="AmazonClientException"/> for client-side identity/transport failures).
    /// In AWS SDK v4 these are sibling types rooted at <see cref="Exception"/>, so both
    /// must be caught explicitly — the adapter comments promise uncertain-submit and
    /// status-preservation behavior for the entire class.
    /// </summary>
    private static bool IsAwsRuntimeException(Exception ex)
        => ex is AmazonServiceException or AmazonClientException;

    private static bool IsSubmissionOutcomeUncertain(Exception ex)
    {
        // AmazonClientException (credential resolution, DNS, socket) never reached AWS —
        // the SubmitJob call did not get far enough to be rejected, so the outcome is
        // ambiguous by definition. In AWS SDK v4 AmazonClientException and
        // AmazonServiceException are sibling types rooted at Exception, so the check
        // here is exclusive.
        if (ex is AmazonClientException)
        {
            return true;
        }

        if (ex is not AmazonServiceException serviceEx)
        {
            return false;
        }

        // A zero-valued StatusCode means no HTTP response reached the SDK. 408/429/5xx
        // mean the provider may still have accepted the SubmitJob call despite returning
        // an error — treat as ambiguous and defer to the reconciler instead of stamping
        // the durable record terminal Failed.
        if ((int)serviceEx.StatusCode == 0)
        {
            return true;
        }

        return serviceEx.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            || (int)serviceEx.StatusCode >= 500;
    }

    public string BackendName => AdapterBackendName;

    public BatchComputeTargetKind TargetKind => BatchComputeTargetKind.AwsBatch;

    public Task<BatchComputeBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CapabilitiesSnapshot);

    public async Task<BatchComputeSubmissionResult> StartAsync(
        ExecutionJobRecord job,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();

        var parameters = job.Spec.Parameters;
        var jobDefinition = GetRequiredParameter(parameters, AwsBatchParameterKeys.JobDefinitionArn);
        var jobQueue = GetRequiredParameter(parameters, AwsBatchParameterKeys.JobQueueArn);
        var region = GetOptionalParameter(parameters, AwsBatchParameterKeys.Region);

        var jobName = BuildJobName(job.OperationId, job.AttemptCount);
        var submission = new AwsBatchJobSubmission
        {
            JobName = jobName,
            JobDefinition = jobDefinition,
            JobQueue = jobQueue,
            EnvironmentOverrides = BuildEnvironmentOverrides(job),
            Vcpus = TryParseInt(parameters, AwsBatchParameterKeys.Vcpus),
            MemoryMib = TryParseInt(parameters, AwsBatchParameterKeys.MemoryMib),
            GpuCount = TryParseInt(parameters, AwsBatchParameterKeys.GpuCount),
            AttemptDurationSeconds = TryParseInt(parameters, AwsBatchParameterKeys.TimeoutSeconds),
            RetryAttempts = TryParseInt(parameters, AwsBatchParameterKeys.RetryAttempts),
            ShareIdentifier = GetOptionalParameter(parameters, AwsBatchParameterKeys.ShareIdentifier)
        };

        try
        {
            var result = await batchClient.SubmitJobAsync(submission, region, cancellationToken).ConfigureAwait(false);
            Log.BatchJobSubmitted(logger, job.OperationId, result.JobId, jobQueue, jobDefinition);
            ControlPlaneTelemetry.RecordExecutionSubmission(job);

            return new BatchComputeSubmissionResult
            {
                Status = ExecutionJobStatus.Queued,
                ProviderOperationId = result.JobId,
                Message = $"Submitted AWS Batch job '{result.JobName}' ({result.JobId}) to queue '{jobQueue}'."
            };
        }
        catch (Exception ex) when (IsAwsRuntimeException(ex) && IsSubmissionOutcomeUncertain(ex))
        {
            // Transport-ambiguous submit (5xx/429/408/credential/network): AWS Batch may
            // have accepted the job even though we never got a response. Preserve the
            // deterministic JobName as a discovery key under the pending marker so the
            // reconciler can verify acceptance via ListJobs on the next cycle instead of
            // stamping the durable record terminal Failed.
            Log.BatchJobSubmissionUncertain(logger, job.OperationId, jobName, ex.Message);

            return new BatchComputeSubmissionResult
            {
                Status = ExecutionJobStatus.Queued,
                ProviderOperationId = PendingSubmissionMarkerPrefix + jobName,
                Message = $"AWS Batch submission outcome is uncertain for job '{jobName}' in queue '{jobQueue}': {ex.Message}. Reconciliation will verify whether the provider accepted the job."
            };
        }
        catch (AmazonServiceException ex)
        {
            // Definite provider rejection (4xx with a real status code): fail fast so
            // operators get an actionable message instead of the job getting stuck in
            // Queued retries. Pure AmazonClientException (identity/transport) is always
            // uncertain and takes the branch above, so only resolved service-level
            // rejections reach this catch.
            Log.BatchJobSubmissionFailed(logger, job.OperationId, jobName, ex.Message);

            return new BatchComputeSubmissionResult
            {
                Status = ExecutionJobStatus.Failed,
                ProviderOperationId = null,
                Message = $"AWS Batch rejected job submission: {ex.Message}"
            };
        }
    }

    public async Task<BatchComputeObservation> ObserveAsync(
        ExecutionJobRecord job,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();

        var providerId = job.ProviderOperationId;
        var region = GetOptionalParameter(job.Spec.Parameters, AwsBatchParameterKeys.Region);

        if (string.IsNullOrWhiteSpace(providerId))
        {
            // A record that already crossed the remote-start boundary but lost its provider
            // id (crash or CAS conflict on the post-start write) must not be treated as
            // "not submitted". Recover ownership by running the deterministic per-attempt
            // name through the pending-discovery path: ListJobsByName will bind to the
            // real AWS Batch job if the provider accepted the submission.
            if (TryDeriveOrphanedSubmissionName(job, out var orphanedJobName))
            {
                return await ObservePendingDiscoveryAsync(job, orphanedJobName, region, cancellationToken).ConfigureAwait(false);
            }

            return new BatchComputeObservation
            {
                Status = job.Status,
                Message = "AWS Batch job has not been submitted yet."
            };
        }

        if (TryExtractPendingJobName(providerId, out var pendingJobName))
        {
            return await ObservePendingDiscoveryAsync(job, pendingJobName, region, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var state = await batchClient.DescribeJobAsync(providerId, region, cancellationToken).ConfigureAwait(false);
            if (state == null)
            {
                Log.BatchJobNotFound(logger, job.OperationId, providerId);
                return new BatchComputeObservation
                {
                    Status = ExecutionJobStatus.Failed,
                    ProviderOperationId = providerId,
                    Message = $"AWS Batch job '{providerId}' was not found."
                };
            }

            var mapped = AwsBatchStateMapper.MapStatusWithReason(state.Status, state.StatusReason);
            var message = BuildObservationMessage(state);
            Log.BatchJobObserved(logger, job.OperationId, providerId, state.Status ?? "UNKNOWN", mapped.ToString());

            return new BatchComputeObservation
            {
                Status = mapped,
                ProviderOperationId = providerId,
                Message = message
            };
        }
        catch (Exception ex) when (IsAwsRuntimeException(ex))
        {
            // Preserve durable state on provider/transport/auth failures, covering both
            // AmazonServiceException (HTTP-level errors) and AmazonClientException
            // (credential resolution, DNS, socket). Those are sibling types in AWS SDK
            // v4, so both must be caught here; if either threw through, the reconciler's
            // generic catch would stamp the record terminal Failed even though the AWS
            // Batch job could still be running.
            Log.BatchJobObservationFailed(logger, job.OperationId, providerId, ex.Message);
            return new BatchComputeObservation
            {
                Status = job.Status,
                ProviderOperationId = providerId,
                PercentComplete = job.PercentComplete,
                Message = $"AWS Batch observation failed: {ex.Message}"
            };
        }
    }

    private async Task<BatchComputeObservation> ObservePendingDiscoveryAsync(
        ExecutionJobRecord job,
        string pendingJobName,
        string? region,
        CancellationToken cancellationToken)
    {
        var jobQueue = GetOptionalParameter(job.Spec.Parameters, AwsBatchParameterKeys.JobQueueArn);
        if (string.IsNullOrWhiteSpace(jobQueue))
        {
            // Discovery requires the queue ARN; without it we cannot disambiguate. Leave the
            // record nonterminal so an operator can repair the workload definition.
            return new BatchComputeObservation
            {
                Status = job.Status,
                ProviderOperationId = job.ProviderOperationId,
                PercentComplete = job.PercentComplete,
                Message = $"AWS Batch submission outcome for '{pendingJobName}' cannot be verified without a job queue."
            };
        }

        try
        {
            var matches = await batchClient
                .ListJobsByNameAsync(jobQueue, pendingJobName, region, cancellationToken)
                .ConfigureAwait(false);
            if (matches.Count == 0)
            {
                if (HasPendingDiscoveryExpired(job))
                {
                    // Past the bounded verification window the provider never acknowledged
                    // the submission: transition to a terminal state so the job stops
                    // polling indefinitely. Mirror Azure Batch's missing-registration rule
                    // (Cancelled when the durable record already carries a cancel signal,
                    // Failed otherwise).
                    return job.CancellationRequestedAt.HasValue
                        ? new BatchComputeObservation
                        {
                            Status = ExecutionJobStatus.Cancelled,
                            ProviderOperationId = job.ProviderOperationId,
                            PercentComplete = 100,
                            Message = $"AWS Batch submission for '{pendingJobName}' never registered with the provider before cancellation completed."
                        }
                        : new BatchComputeObservation
                        {
                            Status = ExecutionJobStatus.Failed,
                            ProviderOperationId = job.ProviderOperationId,
                            PercentComplete = 100,
                            Message = $"AWS Batch submission for '{pendingJobName}' did not register with the provider within the verification window."
                        };
                }

                return new BatchComputeObservation
                {
                    Status = job.Status,
                    ProviderOperationId = job.ProviderOperationId,
                    PercentComplete = job.PercentComplete,
                    Message = $"AWS Batch submission outcome for '{pendingJobName}' is still being verified; the provider has not yet acknowledged the job."
                };
            }

            var summary = matches[0];
            var mapped = AwsBatchStateMapper.MapStatusWithReason(summary.Status, summary.StatusReason);
            Log.BatchJobDiscoveryResolved(
                logger,
                job.OperationId,
                pendingJobName,
                summary.JobId,
                summary.Status ?? "UNKNOWN");

            return new BatchComputeObservation
            {
                Status = mapped,
                ProviderOperationId = summary.JobId,
                Message = $"Discovered AWS Batch job '{summary.JobId}' by name '{pendingJobName}' in state {summary.Status ?? "UNKNOWN"}."
            };
        }
        catch (Exception ex) when (IsAwsRuntimeException(ex))
        {
            Log.BatchJobObservationFailed(logger, job.OperationId, pendingJobName, ex.Message);
            return new BatchComputeObservation
            {
                Status = job.Status,
                ProviderOperationId = job.ProviderOperationId,
                PercentComplete = job.PercentComplete,
                Message = $"AWS Batch discovery failed for pending job '{pendingJobName}': {ex.Message}"
            };
        }
    }

    private static bool HasPendingDiscoveryExpired(ExecutionJobRecord job)
    {
        // Grace window is measured from the durable record's last observable change.
        // The reconciler's no-op merge preserves UpdatedAt when an observation returns
        // unchanged fields, so repeated "still verifying" ticks do not reset the window.
        return DateTimeOffset.UtcNow - job.UpdatedAt >= PendingDiscoveryGracePeriod;
    }

    public async Task<BatchComputeObservation> CancelAsync(
        ExecutionJobRecord job,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();

        var providerId = job.ProviderOperationId;
        var region = GetOptionalParameter(job.Spec.Parameters, AwsBatchParameterKeys.Region);

        if (string.IsNullOrWhiteSpace(providerId))
        {
            // A record that already crossed the remote-start boundary but lost its provider
            // id (crash or CAS conflict on the post-start write) must not be treated as
            // "cancelled before submission" — that would orphan an accepted AWS Batch job.
            // Route through the discovery cancel path using the deterministic per-attempt
            // name so ListJobsByName can rediscover and cancel the real provider job.
            if (TryDeriveOrphanedSubmissionName(job, out var orphanedJobName))
            {
                return await CancelPendingDiscoveryAsync(job, orphanedJobName, region, cancellationToken).ConfigureAwait(false);
            }

            return new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Cancelled,
                Message = "AWS Batch job was cancelled before submission."
            };
        }

        if (TryExtractPendingJobName(providerId, out var pendingJobName))
        {
            return await CancelPendingDiscoveryAsync(job, pendingJobName, region, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var state = await batchClient.DescribeJobAsync(providerId, region, cancellationToken).ConfigureAwait(false);
            if (state == null)
            {
                return new BatchComputeObservation
                {
                    Status = ExecutionJobStatus.Cancelled,
                    ProviderOperationId = providerId,
                    Message = $"AWS Batch job '{providerId}' was not found; treating as cancelled."
                };
            }

            var currentMapped = AwsBatchStateMapper.MapStatusWithReason(state.Status, state.StatusReason);
            if (AwsBatchStateMapper.IsTerminal(currentMapped))
            {
                return new BatchComputeObservation
                {
                    Status = currentMapped,
                    ProviderOperationId = providerId,
                    Message = BuildObservationMessage(state)
                };
            }

            if (AwsBatchStateMapper.CanCancelWithoutTerminate(state.Status))
            {
                await batchClient.CancelJobAsync(providerId, AwsBatchStateMapper.CancelReason, region, cancellationToken).ConfigureAwait(false);
                Log.BatchJobCancelled(logger, job.OperationId, providerId, state.Status ?? "UNKNOWN");
            }
            else
            {
                await batchClient.TerminateJobAsync(providerId, AwsBatchStateMapper.CancelReason, region, cancellationToken).ConfigureAwait(false);
                Log.BatchJobTerminated(logger, job.OperationId, providerId, state.Status ?? "UNKNOWN");
            }

            // Re-observe so we only report terminal Cancelled once AWS has actually reached a
            // terminal state. If AWS has not yet transitioned (e.g. TerminateJob is still propagating
            // SIGTERM), surface the current non-terminal state so the reconciler keeps polling.
            var postCancelState = await batchClient.DescribeJobAsync(providerId, region, cancellationToken).ConfigureAwait(false);
            if (postCancelState == null)
            {
                return new BatchComputeObservation
                {
                    Status = ExecutionJobStatus.Cancelled,
                    ProviderOperationId = providerId,
                    Message = $"AWS Batch job '{providerId}' disappeared after cancellation; treating as cancelled."
                };
            }

            var postMapped = AwsBatchStateMapper.MapStatusWithReason(postCancelState.Status, postCancelState.StatusReason);
            var message = AwsBatchStateMapper.IsTerminal(postMapped)
                ? $"AWS Batch job '{providerId}' reached {postCancelState.Status ?? "UNKNOWN"} after cancellation from state {state.Status ?? "UNKNOWN"}."
                : $"AWS Batch job '{providerId}' cancellation requested from state {state.Status ?? "UNKNOWN"}; provider still at {postCancelState.Status ?? "UNKNOWN"}.";

            return new BatchComputeObservation
            {
                Status = postMapped,
                ProviderOperationId = providerId,
                Message = message
            };
        }
        catch (Exception ex) when (IsAwsRuntimeException(ex))
        {
            // Preserve durable state on provider/transport/auth failures, covering both
            // AmazonServiceException (HTTP-level errors) and AmazonClientException
            // (credential resolution, DNS, socket). Without this catch, a transient AWS
            // SDK blip during a user cancel would surface as an unhandled 500 to the
            // caller and stamp the durable record terminal Failed via the reconciler's
            // generic catch.
            Log.BatchJobCancellationFailed(logger, job.OperationId, providerId, ex.Message);
            return new BatchComputeObservation
            {
                Status = job.Status,
                ProviderOperationId = providerId,
                PercentComplete = job.PercentComplete,
                Message = $"AWS Batch cancellation failed: {ex.Message}"
            };
        }
    }

    private async Task<BatchComputeObservation> CancelPendingDiscoveryAsync(
        ExecutionJobRecord job,
        string pendingJobName,
        string? region,
        CancellationToken cancellationToken)
    {
        var jobQueue = GetOptionalParameter(job.Spec.Parameters, AwsBatchParameterKeys.JobQueueArn);
        if (string.IsNullOrWhiteSpace(jobQueue))
        {
            // Without a queue we cannot discover the pending job. Treat as cancelled-before-
            // submission rather than escalating to Failed: the caller is asking us to cancel
            // and we never confirmed provider ownership.
            return new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Cancelled,
                ProviderOperationId = job.ProviderOperationId,
                Message = $"AWS Batch job '{pendingJobName}' has no queue for discovery; treating cancellation as completed."
            };
        }

        try
        {
            var matches = await batchClient
                .ListJobsByNameAsync(jobQueue, pendingJobName, region, cancellationToken)
                .ConfigureAwait(false);
            if (matches.Count == 0)
            {
                if (HasPendingDiscoveryExpired(job))
                {
                    // After the bounded verification window the provider never acknowledged
                    // the submission: treat the cancel as completed. Terminalizing earlier
                    // would risk orphaning a real AWS Batch job that surfaces after the
                    // first empty ListJobsByName result — the uncertain-submit path keeps
                    // the record active precisely because AWS may still accept the job.
                    return new BatchComputeObservation
                    {
                        Status = ExecutionJobStatus.Cancelled,
                        ProviderOperationId = job.ProviderOperationId,
                        PercentComplete = 100,
                        Message = $"AWS Batch submission for '{pendingJobName}' never registered with the provider before cancellation completed."
                    };
                }

                // Within the grace window keep the record non-terminal. The durable
                // CancellationRequestedAt signal is already set, so the reconciler will
                // keep calling us; we terminalize only once we can confirm AWS never
                // accepted the submission or a concrete provider job is discovered and
                // cancelled via the normal path.
                return new BatchComputeObservation
                {
                    Status = job.Status,
                    ProviderOperationId = job.ProviderOperationId,
                    PercentComplete = job.PercentComplete,
                    Message = $"AWS Batch submission for '{pendingJobName}' is still being verified before cancellation completes."
                };
            }

            var summary = matches[0];
            Log.BatchJobDiscoveryResolved(
                logger,
                job.OperationId,
                pendingJobName,
                summary.JobId,
                summary.Status ?? "UNKNOWN");

            // Re-enter the normal cancel path now that we have a concrete provider id. The
            // hydrated record drops the pending marker so the recursive call takes the
            // DescribeJob/CancelJob/TerminateJob branch.
            var hydrated = job with { ProviderOperationId = summary.JobId };
            return await CancelAsync(hydrated, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsAwsRuntimeException(ex))
        {
            Log.BatchJobCancellationFailed(logger, job.OperationId, pendingJobName, ex.Message);
            return new BatchComputeObservation
            {
                Status = job.Status,
                ProviderOperationId = job.ProviderOperationId,
                PercentComplete = job.PercentComplete,
                Message = $"AWS Batch discovery failed during cancellation for pending job '{pendingJobName}': {ex.Message}"
            };
        }
    }

    internal static string BuildJobName(string operationId, int attemptCount)
    {
        // AWS Batch job names are 1-128 chars, [A-Za-z0-9_-]. Embed a per-attempt
        // suffix so retry resubmissions use a distinct provider name; otherwise the
        // pending-marker ListJobsByName discovery path could bind to an earlier
        // attempt that still exists at AWS under the same operation id. AWS ListJobs
        // documents JOB_NAME returns matching jobs rather than a unique id:
        // https://docs.aws.amazon.com/batch/latest/APIReference/API_ListJobs.html.
        var suffix = "-a" + attemptCount.ToString(CultureInfo.InvariantCulture);
        var bodyBudget = Math.Max(1, 128 - suffix.Length);

        var span = operationId.AsSpan();
        var bodyLen = Math.Min(span.Length, bodyBudget);
        var body = new char[bodyLen];
        var index = 0;
        for (var i = 0; i < bodyLen; i++)
        {
            var ch = span[i];
            body[index++] = char.IsAsciiLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '-';
        }

        var head = index == 0 ? "honua-job" : new string(body, 0, index);
        return head + suffix;
    }

    private static List<AwsBatchEnvironmentOverride> BuildEnvironmentOverrides(ExecutionJobRecord job)
    {
        var overrides = new List<AwsBatchEnvironmentOverride>
        {
            new("HONUA_OPERATION_ID", job.OperationId),
            new("HONUA_WORKLOAD_NAME", job.Spec.WorkloadName),
            new("HONUA_JOB_KIND", job.Spec.Kind.ToString())
        };

        if (!string.IsNullOrWhiteSpace(job.Spec.WorkloadId))
        {
            overrides.Add(new("HONUA_WORKLOAD_ID", job.Spec.WorkloadId));
        }

        if (!string.IsNullOrWhiteSpace(job.Spec.RuntimeProfile))
        {
            overrides.Add(new("HONUA_RUNTIME_PROFILE", job.Spec.RuntimeProfile));
        }

        foreach (var entry in job.Spec.Parameters)
        {
            if (entry.Key.StartsWith("batch.", StringComparison.Ordinal))
            {
                continue;
            }

            if (!entry.Key.StartsWith("env.", StringComparison.Ordinal))
            {
                continue;
            }

            var name = entry.Key["env.".Length..];
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            overrides.Add(new(name, entry.Value ?? string.Empty));
        }

        return overrides;
    }

    private static string BuildObservationMessage(AwsBatchJobState state)
    {
        if (!string.IsNullOrWhiteSpace(state.StatusReason))
        {
            return $"AWS Batch job '{state.JobId}' status={state.Status ?? "UNKNOWN"}: {state.StatusReason}";
        }

        return $"AWS Batch job '{state.JobId}' status={state.Status ?? "UNKNOWN"}";
    }

    private static string GetRequiredParameter(IReadOnlyDictionary<string, string> parameters, string key)
    {
        if (!parameters.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"AWS Batch submission requires parameter '{key}'.");
        }

        return value.Trim();
    }

    private static string? GetOptionalParameter(IReadOnlyDictionary<string, string> parameters, string key)
        => parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    private static int? TryParseInt(IReadOnlyDictionary<string, string> parameters, string key)
    {
        if (!parameters.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    internal static partial class Log
    {
        [LoggerMessage(9040, LogLevel.Information, "Submitted AWS Batch job {OperationId} ({ProviderJobId}) to queue {JobQueue} using definition {JobDefinition}")]
        public static partial void BatchJobSubmitted(ILogger logger, string operationId, string providerJobId, string jobQueue, string jobDefinition);

        [LoggerMessage(9041, LogLevel.Debug, "Observed AWS Batch job {OperationId} ({ProviderJobId}) at state {AwsStatus} (mapped to {MappedStatus})")]
        public static partial void BatchJobObserved(ILogger logger, string operationId, string providerJobId, string awsStatus, string mappedStatus);

        [LoggerMessage(9042, LogLevel.Warning, "AWS Batch job {OperationId} provider id {ProviderJobId} was not found during observation")]
        public static partial void BatchJobNotFound(ILogger logger, string operationId, string providerJobId);

        [LoggerMessage(9043, LogLevel.Information, "Cancelled AWS Batch job {OperationId} ({ProviderJobId}) from state {AwsStatus}")]
        public static partial void BatchJobCancelled(ILogger logger, string operationId, string providerJobId, string awsStatus);

        [LoggerMessage(9044, LogLevel.Information, "Terminated AWS Batch job {OperationId} ({ProviderJobId}) from state {AwsStatus}")]
        public static partial void BatchJobTerminated(ILogger logger, string operationId, string providerJobId, string awsStatus);

        [LoggerMessage(9045, LogLevel.Warning, "AWS Batch submission outcome is uncertain for execution job {OperationId} (job name {JobName}): {ErrorMessage}")]
        public static partial void BatchJobSubmissionUncertain(ILogger logger, string operationId, string jobName, string errorMessage);

        [LoggerMessage(9046, LogLevel.Warning, "AWS Batch rejected submission for execution job {OperationId} (job name {JobName}): {ErrorMessage}")]
        public static partial void BatchJobSubmissionFailed(ILogger logger, string operationId, string jobName, string errorMessage);

        [LoggerMessage(9047, LogLevel.Debug, "AWS Batch observation failed for execution job {OperationId} ({ProviderJobId}): {ErrorMessage}")]
        public static partial void BatchJobObservationFailed(ILogger logger, string operationId, string providerJobId, string errorMessage);

        [LoggerMessage(9048, LogLevel.Warning, "AWS Batch cancellation failed for execution job {OperationId} ({ProviderJobId}): {ErrorMessage}")]
        public static partial void BatchJobCancellationFailed(ILogger logger, string operationId, string providerJobId, string errorMessage);

        [LoggerMessage(9049, LogLevel.Information, "Discovered AWS Batch job for execution job {OperationId} (pending name {PendingJobName}) as {ProviderJobId} in state {AwsStatus}")]
        public static partial void BatchJobDiscoveryResolved(ILogger logger, string operationId, string pendingJobName, string providerJobId, string awsStatus);
    }
}
