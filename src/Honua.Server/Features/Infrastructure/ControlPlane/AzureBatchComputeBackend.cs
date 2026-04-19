// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Features.Infrastructure.ControlPlane;

/// <summary>
/// Azure Batch execution adapter. Maps the canonical <see cref="ExecutionJobRecord"/>
/// lifecycle onto Azure Batch jobs, tasks, and pools while preserving claim, heartbeat,
/// retry, cancellation, and artifact-publication semantics provided by the shared
/// worker substrate.
/// </summary>
/// <remarks>
/// Azure Batch is an optional backend: the baseline lightweight runtime remains valid
/// without this adapter registered. Heavyweight (GDAL-class) workloads opt in by
/// pointing a workload at a heavy-image pool through the <c>azure.batch.pool_id</c>
/// parameter and an appropriate <c>azure.batch.container_image</c>.
/// </remarks>
internal sealed partial class AzureBatchComputeBackend(
    IAzureBatchClient batchClient,
    ILogger<AzureBatchComputeBackend> logger) : IBatchComputeBackend
{
    internal const string BackendIdentifier = "honua-azure-batch";
    internal static TimeSpan MissingRegistrationGracePeriod => TimeSpan.FromMinutes(2);

    private const string ParamAccountUrl = "azure.batch.account_url";
    private const string ParamPoolId = "azure.batch.pool_id";
    private const string ParamContainerImage = "azure.batch.container_image";
    private const string ParamContainerRunOptions = "azure.batch.container_run_options";
    private const string ParamCommandLine = "azure.batch.command_line";
    private const string ParamMaxTaskRetryCount = "azure.batch.max_task_retry_count";
    private const string ParamTaskTimeoutMinutes = "azure.batch.task_timeout_minutes";
    private const string ParamOutputContainerUrl = "azure.storage.output_container_url";
    private const string EnvPrefix = "azure.batch.env.";

    public string BackendName => BackendIdentifier;

    public BatchComputeTargetKind TargetKind => BatchComputeTargetKind.AzureBatch;

    public Task<BatchComputeBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new BatchComputeBackendCapabilities
        {
            SupportsCancellation = true,
            SupportsLogStreaming = false,
            SupportsProgressPolling = true,
            SupportsRetry = true,
            SupportsArtifactStaging = true
        });

    public async Task<BatchComputeSubmissionResult> StartAsync(
        ExecutionJobRecord job,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();

        var parameters = job.Spec.Parameters;
        var accountUrl = RequireParameter(parameters, ParamAccountUrl);
        var poolId = RequireParameter(parameters, ParamPoolId);
        var commandLine = GetParameter(parameters, ParamCommandLine) ?? DefaultCommandLine;

        var submission = new AzureBatchJobSubmission
        {
            AccountUrl = accountUrl,
            JobId = BuildJobId(job.OperationId),
            PoolId = poolId,
            CommandLine = commandLine,
            ContainerImage = GetParameter(parameters, ParamContainerImage) ?? job.Spec.Artifact,
            ContainerRunOptions = GetParameter(parameters, ParamContainerRunOptions),
            MaxTaskRetryCount = ParseIntOrDefault(parameters, ParamMaxTaskRetryCount, 2),
            TaskTimeout = ParseTimeoutMinutes(parameters, ParamTaskTimeoutMinutes, defaultMinutes: 120),
            OutputContainerUrl = GetParameter(parameters, ParamOutputContainerUrl),
            EnvironmentSettings = CollectEnvironmentSettings(job, parameters)
        };

        // Validate the pool up front: empty/non-resizing pools would otherwise cause the
        // task to queue indefinitely in `preparing` with no operator signal. Design brief
        // calls this out as the StartAsync mitigation for the zero-node pool risk.
        var poolValidation = await ValidatePoolAsync(accountUrl, poolId, submission.JobId, cancellationToken)
            .ConfigureAwait(false);
        if (poolValidation != null)
        {
            return poolValidation;
        }

        try
        {
            var status = await batchClient.CreateJobAsync(submission, cancellationToken).ConfigureAwait(false);
            Log.JobSubmitted(logger, job.OperationId, submission.JobId, poolId);
            ControlPlaneTelemetry.RecordExecutionSubmission(job);

            return new BatchComputeSubmissionResult
            {
                Status = ExecutionJobStatus.Queued,
                ProviderOperationId = submission.JobId,
                Message = status == HttpStatusCode.Conflict
                    ? $"Azure Batch job '{submission.JobId}' already exists in pool '{poolId}'; resuming observation."
                    : $"Azure Batch job '{submission.JobId}' queued in pool '{poolId}'."
            };
        }
        catch (HttpRequestException ex)
        {
            Log.JobSubmissionFailed(logger, job.OperationId, submission.JobId, ex.Message);
            // Preserve the deterministic JobId even on failure: a timeout or late error may
            // still leave the job accepted at Azure Batch, and we need the id to observe or
            // cancel it during reconciliation rather than orphaning the provider resource.
            if (IsSubmissionOutcomeUncertain(ex))
            {
                return new BatchComputeSubmissionResult
                {
                    Status = ExecutionJobStatus.Queued,
                    ProviderOperationId = submission.JobId,
                    Message = $"Azure Batch submission outcome is uncertain for job '{submission.JobId}': {ex.Message}. Reconciliation will verify whether the provider accepted the job."
                };
            }

            return new BatchComputeSubmissionResult
            {
                Status = ExecutionJobStatus.Failed,
                ProviderOperationId = submission.JobId,
                Message = $"Azure Batch rejected job submission: {ex.Message}"
            };
        }
    }

    public async Task<BatchComputeObservation> ObserveAsync(
        ExecutionJobRecord job,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();

        var parameters = job.Spec.Parameters;
        var accountUrl = RequireParameter(parameters, ParamAccountUrl);
        var providerJobId = job.ProviderOperationId ?? BuildJobId(job.OperationId);

        try
        {
            var state = await batchClient.GetJobStateAsync(accountUrl, providerJobId, cancellationToken).ConfigureAwait(false);
            return MapObservation(job, providerJobId, state);
        }
        catch (HttpRequestException ex)
        {
            Log.JobObservationFailed(logger, job.OperationId, providerJobId, ex.Message);
            return new BatchComputeObservation
            {
                Status = job.Status,
                ProviderOperationId = providerJobId,
                PercentComplete = job.PercentComplete,
                Message = $"Azure Batch observation failed: {ex.Message}"
            };
        }
    }

    public async Task<BatchComputeObservation> CancelAsync(
        ExecutionJobRecord job,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();

        var parameters = job.Spec.Parameters;
        var accountUrl = RequireParameter(parameters, ParamAccountUrl);
        var providerJobId = job.ProviderOperationId ?? BuildJobId(job.OperationId);

        try
        {
            // Azure Batch `Job - Terminate` returns 202 Accepted and the job enters a
            // `terminating` phase before `completed`. Issue the (idempotent) terminate
            // request, then observe the current task state so the reconciler does not
            // stamp the record terminal while Batch is still draining the task.
            // MapObservation routes CompletedFailure back to Cancelled once the task
            // finishes, because the cancellation intent is carried on the record.
            await batchClient.TerminateJobAsync(accountUrl, providerJobId, cancellationToken).ConfigureAwait(false);
            Log.JobCancelled(logger, job.OperationId, providerJobId);

            var state = await batchClient.GetJobStateAsync(accountUrl, providerJobId, cancellationToken).ConfigureAwait(false);
            var observed = MapObservation(job, providerJobId, state);
            return observed with
            {
                Message = IsTerminal(observed.Status)
                    ? observed.Message
                    : $"Azure Batch termination requested for job '{providerJobId}'; {observed.Message}"
            };
        }
        catch (HttpRequestException ex)
        {
            Log.JobCancellationFailed(logger, job.OperationId, providerJobId, ex.Message);
            return new BatchComputeObservation
            {
                Status = job.Status,
                ProviderOperationId = providerJobId,
                PercentComplete = job.PercentComplete,
                Message = $"Azure Batch cancellation failed: {ex.Message}"
            };
        }
    }

    private static bool IsTerminal(ExecutionJobStatus status)
        => status is ExecutionJobStatus.Succeeded
            or ExecutionJobStatus.Failed
            or ExecutionJobStatus.Cancelled;

    internal static BatchComputeObservation MapObservation(
        ExecutionJobRecord job,
        string providerJobId,
        AzureBatchJobState state)
    {
        return state.ExecutionState switch
        {
            AzureBatchTaskExecutionState.NotFound => MapMissingObservation(job, providerJobId),
            AzureBatchTaskExecutionState.Active => new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Queued,
                ProviderOperationId = providerJobId,
                PercentComplete = 0,
                Message = BuildStatusMessage("queued", state)
            },
            AzureBatchTaskExecutionState.Preparing => new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Provisioning,
                ProviderOperationId = providerJobId,
                PercentComplete = 0,
                Message = BuildStatusMessage("preparing", state)
            },
            AzureBatchTaskExecutionState.Running => new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Running,
                ProviderOperationId = providerJobId,
                PercentComplete = null,
                Message = BuildStatusMessage("running", state)
            },
            AzureBatchTaskExecutionState.CompletedSuccess => new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Succeeded,
                ProviderOperationId = providerJobId,
                PercentComplete = 100,
                Message = BuildStatusMessage("succeeded", state)
            },
            AzureBatchTaskExecutionState.Cancelled => new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Cancelled,
                ProviderOperationId = providerJobId,
                PercentComplete = 100,
                Message = BuildStatusMessage("cancelled", state)
            },
            // CompletedFailure. If the canonical record already knows it was cancelled
            // (or has an outstanding cancellation request), keep that intent: Azure Batch
            // represents user termination as a completed failure, so the cancelled signal
            // on the durable record is what distinguishes real failures from cancellations.
            _ when job.Status == ExecutionJobStatus.Cancelled || job.CancellationRequestedAt.HasValue => new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Cancelled,
                ProviderOperationId = providerJobId,
                PercentComplete = 100,
                Message = state.FailureMessage ?? BuildStatusMessage("cancelled", state)
            },
            _ => new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Failed,
                ProviderOperationId = providerJobId,
                PercentComplete = 100,
                Message = state.FailureMessage ?? BuildStatusMessage("failed", state)
            }
        };
    }

    private static BatchComputeObservation MapMissingObservation(ExecutionJobRecord job, string providerJobId)
    {
        if (IsTerminal(job.Status))
        {
            return new BatchComputeObservation
            {
                Status = job.Status,
                ProviderOperationId = providerJobId,
                PercentComplete = job.PercentComplete,
                Message = job.CurrentPhase ?? $"Azure Batch job '{providerJobId}' is no longer visible after reaching terminal state."
            };
        }

        if (HasMissingRegistrationExpired(job))
        {
            return job.CancellationRequestedAt.HasValue
                ? new BatchComputeObservation
                {
                    Status = ExecutionJobStatus.Cancelled,
                    ProviderOperationId = providerJobId,
                    PercentComplete = 100,
                    Message = $"Azure Batch job '{providerJobId}' never registered before cancellation completed."
                }
                : new BatchComputeObservation
                {
                    Status = ExecutionJobStatus.Failed,
                    ProviderOperationId = providerJobId,
                    PercentComplete = 100,
                    Message = $"Azure Batch job '{providerJobId}' did not register with the scheduler after submission."
                };
        }

        return new BatchComputeObservation
        {
            Status = job.Status switch
            {
                ExecutionJobStatus.Provisioning => ExecutionJobStatus.Provisioning,
                ExecutionJobStatus.Running => ExecutionJobStatus.Running,
                _ => ExecutionJobStatus.Queued
            },
            ProviderOperationId = providerJobId,
            PercentComplete = job.PercentComplete,
            Message = $"Azure Batch job '{providerJobId}' has not yet registered with the scheduler."
        };
    }

    private static bool HasMissingRegistrationExpired(ExecutionJobRecord job)
    {
        if (string.IsNullOrWhiteSpace(job.ProviderOperationId) && job.AttemptCount == 0)
        {
            return false;
        }

        return DateTimeOffset.UtcNow - job.UpdatedAt >= MissingRegistrationGracePeriod;
    }

    private static bool IsSubmissionOutcomeUncertain(HttpRequestException ex)
    {
        if (ex.StatusCode is null)
        {
            return true;
        }

        return ex.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            || (int)ex.StatusCode >= 500;
    }

    private static string BuildStatusMessage(string canonicalStatus, AzureBatchJobState state)
    {
        var rawState = string.IsNullOrWhiteSpace(state.RawTaskState) ? "unknown" : state.RawTaskState;
        var retry = state.RetryCount is > 0 ? $" (retries consumed: {state.RetryCount})" : string.Empty;
        var exitCode = state.ExitCode is not null ? $" (exit code: {state.ExitCode})" : string.Empty;
        return $"Azure Batch task is {canonicalStatus}; raw state '{rawState}'{retry}{exitCode}.";
    }

    private async Task<BatchComputeSubmissionResult?> ValidatePoolAsync(
        string accountUrl,
        string poolId,
        string submissionJobId,
        CancellationToken cancellationToken)
    {
        AzureBatchPoolState poolState;
        try
        {
            poolState = await batchClient.GetPoolStateAsync(accountUrl, poolId, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            // Missing pool returns 404 and surfaces as HttpRequestException from the client.
            // Treat any pool lookup failure as a hard submission error so the durable record
            // records an actionable message instead of silently queuing a task that will
            // never execute.
            Log.PoolValidationFailed(logger, poolId, ex.Message);
            return new BatchComputeSubmissionResult
            {
                Status = ExecutionJobStatus.Failed,
                ProviderOperationId = submissionJobId,
                Message = $"Azure Batch pool '{poolId}' is not reachable: {ex.Message}"
            };
        }

        // `deleting`/`upgrading` pools cannot accept new task assignments, and a pool with
        // no current or target nodes will never schedule work. Fail fast with a descriptive
        // message instead of letting the task sit in `preparing` until the operator notices.
        if (!string.IsNullOrWhiteSpace(poolState.State)
            && !string.Equals(poolState.State, "active", StringComparison.OrdinalIgnoreCase))
        {
            Log.PoolValidationRejected(logger, poolId, poolState.State, poolState.AllocationState);
            return new BatchComputeSubmissionResult
            {
                Status = ExecutionJobStatus.Failed,
                ProviderOperationId = submissionJobId,
                Message = $"Azure Batch pool '{poolId}' is in state '{poolState.State}' and cannot accept new tasks."
            };
        }

        var currentNodes = poolState.DedicatedNodes + poolState.LowPriorityNodes;
        var targetNodes = poolState.TargetDedicatedNodes + poolState.TargetLowPriorityNodes;
        if (currentNodes == 0 && targetNodes == 0)
        {
            Log.PoolValidationRejected(logger, poolId, poolState.State ?? "unknown", poolState.AllocationState);
            return new BatchComputeSubmissionResult
            {
                Status = ExecutionJobStatus.Failed,
                ProviderOperationId = submissionJobId,
                Message = $"Azure Batch pool '{poolId}' has no current or target nodes; submission would queue indefinitely."
            };
        }

        return null;
    }

    private static string BuildJobId(string operationId)
    {
        // Azure Batch job ids are limited to 64 chars of URL-safe chars. Canonical
        // operation ids are already URL-safe (guids/slugs); prefix to keep deployments
        // from different environments from colliding within an account.
        var id = $"honua-{operationId}";
        return id.Length <= 64 ? id : id[..64];
    }

    // The default command line resolves workload/job identity from the task environment
    // rather than string-interpolating them, so human-readable workload names (e.g.,
    // "Python GP") do not split into multiple arguments via shell word-splitting.
    // HONUA_WORKLOAD_NAME and HONUA_JOB_ID are always populated in CollectEnvironmentSettings.
    private const string DefaultCommandLine =
        "/bin/bash -c 'exec /app/run-workload --workload \"$HONUA_WORKLOAD_NAME\" --job \"$HONUA_JOB_ID\"'";

    private static Dictionary<string, string> CollectEnvironmentSettings(
        ExecutionJobRecord job,
        IReadOnlyDictionary<string, string> parameters)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HONUA_JOB_ID"] = job.OperationId,
            ["HONUA_WORKLOAD_ID"] = job.Spec.WorkloadId ?? string.Empty,
            ["HONUA_WORKLOAD_NAME"] = job.Spec.WorkloadName,
            ["HONUA_WORKLOAD_KIND"] = job.Spec.Kind.ToString()
        };

        if (!string.IsNullOrWhiteSpace(job.Spec.RuntimeProfile))
        {
            settings["HONUA_RUNTIME_PROFILE"] = job.Spec.RuntimeProfile;
        }

        foreach (var (key, value) in parameters)
        {
            if (!key.StartsWith(EnvPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var envName = key[EnvPrefix.Length..];
            if (!string.IsNullOrWhiteSpace(envName))
            {
                settings[envName] = value;
            }
        }

        return settings;
    }

    private static string? GetParameter(IReadOnlyDictionary<string, string> parameters, string key)
        => parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    private static string RequireParameter(IReadOnlyDictionary<string, string> parameters, string key)
    {
        var value = GetParameter(parameters, key);
        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException(
                $"Azure Batch execution workload is missing required parameter '{key}'.");
        }

        return value;
    }

    private static int ParseIntOrDefault(IReadOnlyDictionary<string, string> parameters, string key, int defaultValue)
    {
        var raw = GetParameter(parameters, key);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed
            : defaultValue;
    }

    private static TimeSpan? ParseTimeoutMinutes(
        IReadOnlyDictionary<string, string> parameters,
        string key,
        int defaultMinutes)
    {
        var raw = GetParameter(parameters, key);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultMinutes > 0 ? TimeSpan.FromMinutes(defaultMinutes) : null;
        }

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) && minutes > 0)
        {
            return TimeSpan.FromMinutes(minutes);
        }

        return defaultMinutes > 0 ? TimeSpan.FromMinutes(defaultMinutes) : null;
    }

    private static partial class Log
    {
        [LoggerMessage(9080, LogLevel.Information, "Submitted execution job {OperationId} as Azure Batch job {JobId} in pool {PoolId}")]
        public static partial void JobSubmitted(ILogger logger, string operationId, string jobId, string poolId);

        [LoggerMessage(9081, LogLevel.Warning, "Azure Batch submission failed for execution job {OperationId} (batch job {JobId}): {ErrorMessage}")]
        public static partial void JobSubmissionFailed(ILogger logger, string operationId, string jobId, string errorMessage);

        [LoggerMessage(9082, LogLevel.Debug, "Azure Batch observation failed for execution job {OperationId} (batch job {JobId}): {ErrorMessage}")]
        public static partial void JobObservationFailed(ILogger logger, string operationId, string jobId, string errorMessage);

        [LoggerMessage(9083, LogLevel.Warning, "Azure Batch cancellation failed for execution job {OperationId} (batch job {JobId}): {ErrorMessage}")]
        public static partial void JobCancellationFailed(ILogger logger, string operationId, string jobId, string errorMessage);

        [LoggerMessage(9084, LogLevel.Information, "Cancelled execution job {OperationId} via Azure Batch job {JobId}")]
        public static partial void JobCancelled(ILogger logger, string operationId, string jobId);

        [LoggerMessage(9085, LogLevel.Warning, "Azure Batch pool {PoolId} validation failed: {ErrorMessage}")]
        public static partial void PoolValidationFailed(ILogger logger, string poolId, string errorMessage);

        [LoggerMessage(9086, LogLevel.Warning, "Azure Batch pool {PoolId} rejected submission (state: {State}, allocationState: {AllocationState})")]
        public static partial void PoolValidationRejected(ILogger logger, string poolId, string state, string? allocationState);
    }
}
