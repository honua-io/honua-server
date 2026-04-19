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
        var commandLine = GetParameter(parameters, ParamCommandLine) ?? BuildDefaultCommandLine(job);

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
            return new BatchComputeSubmissionResult
            {
                Status = ExecutionJobStatus.Failed,
                ProviderOperationId = job.ProviderOperationId,
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
            await batchClient.TerminateJobAsync(accountUrl, providerJobId, cancellationToken).ConfigureAwait(false);
            Log.JobCancelled(logger, job.OperationId, providerJobId);
            return new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Cancelled,
                ProviderOperationId = providerJobId,
                PercentComplete = 100,
                Message = $"Azure Batch job '{providerJobId}' terminated."
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

    internal static BatchComputeObservation MapObservation(
        ExecutionJobRecord job,
        string providerJobId,
        AzureBatchJobState state)
    {
        return state.ExecutionState switch
        {
            AzureBatchTaskExecutionState.NotFound => new BatchComputeObservation
            {
                Status = job.Status is ExecutionJobStatus.Cancelled or ExecutionJobStatus.Succeeded or ExecutionJobStatus.Failed
                    ? job.Status
                    : ExecutionJobStatus.Queued,
                ProviderOperationId = providerJobId,
                PercentComplete = job.PercentComplete,
                Message = $"Azure Batch job '{providerJobId}' has not yet registered with the scheduler."
            },
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
            // CompletedFailure. If the canonical record already knows it was cancelled, keep
            // that intent: Azure Batch represents user termination as a completed failure.
            _ when job.Status == ExecutionJobStatus.Cancelled => new BatchComputeObservation
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

    private static string BuildStatusMessage(string canonicalStatus, AzureBatchJobState state)
    {
        var rawState = string.IsNullOrWhiteSpace(state.RawTaskState) ? "unknown" : state.RawTaskState;
        var retry = state.RetryCount is > 0 ? $" (retries consumed: {state.RetryCount})" : string.Empty;
        var exitCode = state.ExitCode is not null ? $" (exit code: {state.ExitCode})" : string.Empty;
        return $"Azure Batch task is {canonicalStatus}; raw state '{rawState}'{retry}{exitCode}.";
    }

    private static string BuildJobId(string operationId)
    {
        // Azure Batch job ids are limited to 64 chars of URL-safe chars. Canonical
        // operation ids are already URL-safe (guids/slugs); prefix to keep deployments
        // from different environments from colliding within an account.
        var id = $"honua-{operationId}";
        return id.Length <= 64 ? id : id[..64];
    }

    private static string BuildDefaultCommandLine(ExecutionJobRecord job)
        => $"/bin/bash -c \"/app/run-workload --workload {job.Spec.WorkloadName} --job {job.OperationId}\"";

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
    }
}
