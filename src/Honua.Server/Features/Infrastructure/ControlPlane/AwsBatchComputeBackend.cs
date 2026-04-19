// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
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

    private static readonly BatchComputeBackendCapabilities CapabilitiesSnapshot = new()
    {
        SupportsCancellation = true,
        SupportsProgressPolling = true,
        SupportsRetry = true,
        SupportsLogStreaming = false,
        SupportsArtifactStaging = false
    };

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

        var submission = new AwsBatchJobSubmission
        {
            JobName = BuildJobName(job.OperationId),
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

        var result = await batchClient.SubmitJobAsync(submission, region, cancellationToken).ConfigureAwait(false);
        Log.BatchJobSubmitted(logger, job.OperationId, result.JobId, jobQueue, jobDefinition);

        return new BatchComputeSubmissionResult
        {
            Status = ExecutionJobStatus.Queued,
            ProviderOperationId = result.JobId,
            Message = $"Submitted AWS Batch job '{result.JobName}' ({result.JobId}) to queue '{jobQueue}'."
        };
    }

    public async Task<BatchComputeObservation> ObserveAsync(
        ExecutionJobRecord job,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();

        var providerId = job.ProviderOperationId;
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return new BatchComputeObservation
            {
                Status = job.Status,
                Message = "AWS Batch job has not been submitted yet."
            };
        }

        var region = GetOptionalParameter(job.Spec.Parameters, AwsBatchParameterKeys.Region);
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

    public async Task<BatchComputeObservation> CancelAsync(
        ExecutionJobRecord job,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();

        var providerId = job.ProviderOperationId;
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Cancelled,
                Message = "AWS Batch job was cancelled before submission."
            };
        }

        var region = GetOptionalParameter(job.Spec.Parameters, AwsBatchParameterKeys.Region);
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

    private static string BuildJobName(string operationId)
    {
        // AWS Batch job names must be 1-128 chars, only letters, numbers, hyphens, and underscores.
        var span = operationId.AsSpan();
        var buffer = new char[Math.Min(span.Length, 128)];
        var index = 0;
        for (var i = 0; i < span.Length && index < buffer.Length; i++)
        {
            var ch = span[i];
            if (char.IsAsciiLetterOrDigit(ch) || ch == '-' || ch == '_')
            {
                buffer[index++] = ch;
            }
            else
            {
                buffer[index++] = '-';
            }
        }

        if (index == 0)
        {
            return "honua-job";
        }

        return new string(buffer, 0, index);
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
    }
}
