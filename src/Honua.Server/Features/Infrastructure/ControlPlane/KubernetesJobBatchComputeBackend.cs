// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.ControlPlane;

/// <summary>
/// Batch compute adapter that executes canonical execution jobs on a Kubernetes
/// cluster by translating the <see cref="ExecutionJobRecord"/> into a
/// <c>batch/v1</c> Job and projecting Job + Pod lifecycle back onto
/// <see cref="ExecutionJobStatus"/>. The reconciler owns retries and timeouts;
/// the adapter keeps Jobs at <c>backoffLimit: 0</c> so the canonical runtime
/// remains the single source of truth for retry semantics.
/// </summary>
internal sealed partial class KubernetesJobBatchComputeBackend(
    IKubernetesJobClient jobClient,
    IOptionsMonitor<KubernetesExecutionOptions> options,
    ILogger<KubernetesJobBatchComputeBackend> logger) : IBatchComputeBackend
{
    internal const string BackendId = "honua-kubernetes-job";

    private const string OperationIdLabel = "honua.io/operation-id";
    private const string WorkloadLabel = "honua.io/workload";
    private const string WorkloadKindLabel = "honua.io/workload-kind";
    private const string ManagedByLabel = "app.kubernetes.io/managed-by";
    private const string ManagedByValue = "honua-controlplane";

    public string BackendName => BackendId;

    public BatchComputeTargetKind TargetKind => BatchComputeTargetKind.KubernetesJob;

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

        using var activity = ControlPlaneTelemetry.StartExecutionActivity(
            ControlPlaneTelemetry.Activities.ExecutionStart, job);

        var image = ResolveImage(job);
        if (string.IsNullOrWhiteSpace(image))
        {
            Log.MissingImage(logger, job.OperationId);
            ControlPlaneTelemetry.RecordExecutionRequest(job, "start", "missing_image");
            return new BatchComputeSubmissionResult
            {
                Status = ExecutionJobStatus.Failed,
                Message = "No container image resolved for the Kubernetes execution job."
            };
        }

        var manifest = BuildManifest(job, image);

        try
        {
            var result = await jobClient.CreateJobAsync(manifest, cancellationToken).ConfigureAwait(false);
            if (result.AlreadyExists)
            {
                Log.JobAlreadyExists(logger, job.OperationId, manifest.Namespace, manifest.Name);

                var existing = await jobClient.GetJobAsync(manifest.Namespace, manifest.Name, cancellationToken)
                    .ConfigureAwait(false);
                var snapshot = existing.Snapshot;
                var idempotentStatus = snapshot != null
                    ? MapStatus(snapshot, job.Status)
                    : ExecutionJobStatus.Provisioning;
                ControlPlaneTelemetry.RecordExecutionRequest(job, "start", "already_exists");
                return new BatchComputeSubmissionResult
                {
                    Status = idempotentStatus,
                    ProviderOperationId = snapshot?.Uid ?? job.ProviderOperationId ?? manifest.Name,
                    Message = "Kubernetes Job already exists; treating submission as idempotent."
                };
            }

            ControlPlaneTelemetry.RecordExecutionRequest(job, "start", "submitted");
            return new BatchComputeSubmissionResult
            {
                Status = ExecutionJobStatus.Provisioning,
                ProviderOperationId = result.Snapshot?.Uid ?? manifest.Name,
                Message = $"Submitted Kubernetes Job {manifest.Namespace}/{manifest.Name}."
            };
        }
        catch (Exception ex) when (IsTransportOrConfigFailure(ex))
        {
            Log.SubmissionFailed(logger, job.OperationId, manifest.Namespace, manifest.Name, ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            ControlPlaneTelemetry.RecordExecutionRequest(job, "start", ClassifyFailure(ex));
            return new BatchComputeSubmissionResult
            {
                Status = ExecutionJobStatus.Failed,
                Message = $"Kubernetes Job submission failed: {ex.Message}"
            };
        }
    }

    public async Task<BatchComputeObservation> ObserveAsync(
        ExecutionJobRecord job,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        using var activity = ControlPlaneTelemetry.StartExecutionActivity(
            ControlPlaneTelemetry.Activities.ExecutionObserve, job);

        var coordinates = ResolveCoordinates(job);
        if (coordinates == null)
        {
            ControlPlaneTelemetry.RecordExecutionRequest(job, "observe", "unresolved");
            return new BatchComputeObservation
            {
                Status = job.Status,
                ProviderOperationId = job.ProviderOperationId,
                PercentComplete = job.PercentComplete,
                Message = job.CurrentPhase
            };
        }

        try
        {
            var fetch = await jobClient.GetJobAsync(coordinates.Value.Namespace, coordinates.Value.Name, cancellationToken)
                .ConfigureAwait(false);
            if (fetch.NotFound || fetch.Snapshot == null)
            {
                ControlPlaneTelemetry.RecordExecutionRequest(job, "observe", "not_found");
                return new BatchComputeObservation
                {
                    Status = job.Status == ExecutionJobStatus.Succeeded
                        ? ExecutionJobStatus.Succeeded
                        : ExecutionJobStatus.Failed,
                    ProviderOperationId = job.ProviderOperationId,
                    PercentComplete = job.PercentComplete,
                    Message = "Kubernetes Job is no longer present on the cluster."
                };
            }

            var snapshot = fetch.Snapshot;
            var status = MapStatus(snapshot, job.Status);

            string? message = null;
            if (status == ExecutionJobStatus.Failed)
            {
                var pods = await jobClient
                    .ListPodsAsync(
                        coordinates.Value.Namespace,
                        $"{OperationIdLabel}={job.OperationId}",
                        cancellationToken)
                    .ConfigureAwait(false);
                message = BuildFailureMessage(snapshot, pods);
            }
            else if (status == ExecutionJobStatus.Running)
            {
                message = $"Kubernetes Job running ({snapshot.Active} active pods).";
            }
            else if (status == ExecutionJobStatus.Provisioning)
            {
                message = "Kubernetes Job provisioning.";
            }
            else if (status == ExecutionJobStatus.Succeeded)
            {
                message = "Kubernetes Job completed successfully.";
            }

            ControlPlaneTelemetry.RecordExecutionRequest(job, "observe", status.ToString().ToLowerInvariant());
            return new BatchComputeObservation
            {
                Status = status,
                ProviderOperationId = snapshot.Uid ?? job.ProviderOperationId,
                PercentComplete = ProjectPercentComplete(status, job.PercentComplete),
                Message = message ?? job.CurrentPhase
            };
        }
        catch (Exception ex) when (IsTransportOrConfigFailure(ex))
        {
            Log.ObservationFailed(logger, job.OperationId, coordinates.Value.Namespace, coordinates.Value.Name, ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            ControlPlaneTelemetry.RecordExecutionRequest(job, "observe", ClassifyFailure(ex));
            return new BatchComputeObservation
            {
                Status = job.Status,
                ProviderOperationId = job.ProviderOperationId,
                PercentComplete = job.PercentComplete,
                Message = $"Kubernetes API unavailable: {ex.Message}"
            };
        }
    }

    public async Task<BatchComputeObservation> CancelAsync(
        ExecutionJobRecord job,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        using var activity = ControlPlaneTelemetry.StartExecutionActivity(
            ControlPlaneTelemetry.Activities.ExecutionCancel, job);

        var coordinates = ResolveCoordinates(job);
        if (coordinates == null)
        {
            ControlPlaneTelemetry.RecordExecutionRequest(job, "cancel", "unresolved");
            return new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Cancelled,
                ProviderOperationId = job.ProviderOperationId,
                PercentComplete = job.PercentComplete,
                Message = "Cancelled before a Kubernetes Job could be resolved."
            };
        }

        try
        {
            var result = await jobClient.DeleteJobAsync(coordinates.Value.Namespace, coordinates.Value.Name, cancellationToken)
                .ConfigureAwait(false);
            var message = result.NotFound
                ? "Kubernetes Job was already absent; treating as cancelled."
                : $"Requested cascade deletion of Kubernetes Job {coordinates.Value.Namespace}/{coordinates.Value.Name}.";
            ControlPlaneTelemetry.RecordExecutionRequest(job, "cancel", result.NotFound ? "not_found" : "deleted");
            return new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Cancelled,
                ProviderOperationId = job.ProviderOperationId,
                PercentComplete = job.PercentComplete,
                Message = message
            };
        }
        catch (Exception ex) when (IsTransportOrConfigFailure(ex))
        {
            Log.CancellationFailed(logger, job.OperationId, coordinates.Value.Namespace, coordinates.Value.Name, ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            ControlPlaneTelemetry.RecordExecutionRequest(job, "cancel", ClassifyFailure(ex));
            return new BatchComputeObservation
            {
                Status = job.Status,
                ProviderOperationId = job.ProviderOperationId,
                PercentComplete = job.PercentComplete,
                Message = $"Kubernetes Job cancellation failed: {ex.Message}"
            };
        }
    }

    // Misconfiguration (missing/invalid ApiServerUrl, unreadable bearer token file) surfaces at
    // runtime as InvalidOperationException, UriFormatException, or IOException. Treat those as
    // adapter-surface failures so the reconciler can finalize the job rather than rethrowing
    // into the submission path where the exception would become a 500.
    private static bool IsTransportOrConfigFailure(Exception ex) => ex switch
    {
        HttpRequestException => true,
        InvalidOperationException => true,
        UriFormatException => true,
        IOException => true,
        UnauthorizedAccessException => true,
        _ => false
    };

    private static string ClassifyFailure(Exception ex) => ex switch
    {
        HttpRequestException => "http_error",
        UriFormatException => "config_error",
        InvalidOperationException => "config_error",
        UnauthorizedAccessException => "auth_error",
        IOException => "io_error",
        _ => "error"
    };

    internal static ExecutionJobStatus MapStatus(KubernetesJobStatusSnapshot snapshot, ExecutionJobStatus current)
    {
        if (snapshot.FailedCondition || snapshot.Failed > 0)
        {
            return ExecutionJobStatus.Failed;
        }

        if (snapshot.CompleteCondition || snapshot.Succeeded > 0)
        {
            return ExecutionJobStatus.Succeeded;
        }

        if (snapshot.Active > 0)
        {
            return ExecutionJobStatus.Running;
        }

        return current == ExecutionJobStatus.Running
            ? ExecutionJobStatus.Running
            : ExecutionJobStatus.Provisioning;
    }

    internal KubernetesJobManifest BuildManifest(ExecutionJobRecord job, string image)
    {
        var snapshot = options.CurrentValue;
        var parameters = job.Spec.Parameters;

        var ns = Normalize(parameters.GetValueOrDefault(KubernetesJobParameterKeys.Namespace))
            ?? Normalize(snapshot.DefaultNamespace)
            ?? KubernetesJobClient.TryReadInClusterNamespace()
            ?? "default";

        var name = BuildJobName(job.OperationId);

        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ManagedByLabel] = ManagedByValue,
            [OperationIdLabel] = job.OperationId,
            [WorkloadKindLabel] = job.Spec.Kind.ToString().ToLowerInvariant()
        };
        if (!string.IsNullOrWhiteSpace(job.Spec.WorkloadName))
        {
            labels[WorkloadLabel] = SanitizeLabelValue(job.Spec.WorkloadName);
        }

        var annotations = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(job.Audit.CorrelationId))
        {
            annotations["honua.io/correlation-id"] = job.Audit.CorrelationId!;
        }

        if (!string.IsNullOrWhiteSpace(job.Audit.RequestedBy))
        {
            annotations["honua.io/requested-by"] = job.Audit.RequestedBy!;
        }

        var nodeSelector = ParseMap(parameters.GetValueOrDefault(KubernetesJobParameterKeys.NodeSelector))
            ?? snapshot.DefaultNodeSelector;

        var imagePullSecrets = ParseList(parameters.GetValueOrDefault(KubernetesJobParameterKeys.ImagePullSecrets))
            ?? snapshot.DefaultImagePullSecrets;

        var environmentVariables = BuildEnvironmentVariables(parameters);

        return new KubernetesJobManifest
        {
            Namespace = ns,
            Name = name,
            Image = image,
            Labels = labels,
            Annotations = annotations,
            CpuRequest = Normalize(parameters.GetValueOrDefault(KubernetesJobParameterKeys.CpuRequest))
                ?? snapshot.DefaultCpuRequest,
            CpuLimit = Normalize(parameters.GetValueOrDefault(KubernetesJobParameterKeys.CpuLimit))
                ?? snapshot.DefaultCpuLimit,
            MemoryRequest = Normalize(parameters.GetValueOrDefault(KubernetesJobParameterKeys.MemoryRequest))
                ?? snapshot.DefaultMemoryRequest,
            MemoryLimit = Normalize(parameters.GetValueOrDefault(KubernetesJobParameterKeys.MemoryLimit))
                ?? snapshot.DefaultMemoryLimit,
            NodeSelector = nodeSelector ?? new Dictionary<string, string>(StringComparer.Ordinal),
            ServiceAccount = Normalize(parameters.GetValueOrDefault(KubernetesJobParameterKeys.ServiceAccount))
                ?? snapshot.DefaultServiceAccount,
            ImagePullPolicy = Normalize(parameters.GetValueOrDefault(KubernetesJobParameterKeys.ImagePullPolicy))
                ?? snapshot.DefaultImagePullPolicy,
            ImagePullSecrets = (IReadOnlyList<string>?)imagePullSecrets ?? Array.Empty<string>(),
            ActiveDeadlineSeconds = ResolveActiveDeadlineSeconds(job, parameters, snapshot),
            TtlSecondsAfterFinished = ResolveTtlSeconds(parameters, snapshot),
            EnvironmentVariables = environmentVariables
        };
    }

    private static Dictionary<string, string> BuildEnvironmentVariables(IReadOnlyDictionary<string, string> parameters)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in parameters)
        {
            if (pair.Key.StartsWith(KubernetesJobParameterKeys.EnvironmentPrefix, StringComparison.Ordinal))
            {
                var name = pair.Key[KubernetesJobParameterKeys.EnvironmentPrefix.Length..];
                if (!string.IsNullOrEmpty(name))
                {
                    env[name] = pair.Value;
                }
            }
        }

        return env;
    }

    private static int? ResolveActiveDeadlineSeconds(
        ExecutionJobRecord job,
        IReadOnlyDictionary<string, string> parameters,
        KubernetesExecutionOptions snapshot)
    {
        if (parameters.TryGetValue(KubernetesJobParameterKeys.ActiveDeadlineSeconds, out var explicitValue) &&
            int.TryParse(explicitValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
            parsed > 0)
        {
            return parsed;
        }

        if (job.TimeoutPolicy is { MaxDuration: var maxDuration } && maxDuration > TimeSpan.Zero)
        {
            return (int)Math.Min(int.MaxValue, Math.Ceiling(maxDuration.TotalSeconds));
        }

        return snapshot.DefaultActiveDeadlineSeconds;
    }

    private static int? ResolveTtlSeconds(
        IReadOnlyDictionary<string, string> parameters,
        KubernetesExecutionOptions snapshot)
    {
        if (parameters.TryGetValue(KubernetesJobParameterKeys.TtlSecondsAfterFinished, out var explicitValue) &&
            int.TryParse(explicitValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
            parsed >= 0)
        {
            return parsed;
        }

        return snapshot.DefaultTtlSecondsAfterFinished;
    }

    private string? ResolveImage(ExecutionJobRecord job)
    {
        if (job.Spec.Parameters.TryGetValue(KubernetesJobParameterKeys.Image, out var explicitImage) &&
            !string.IsNullOrWhiteSpace(explicitImage))
        {
            return explicitImage.Trim();
        }

        if (!string.IsNullOrWhiteSpace(job.Spec.Artifact))
        {
            return job.Spec.Artifact!.Trim();
        }

        var snapshot = options.CurrentValue;
        return Normalize(snapshot.DefaultImage);
    }

    private (string Namespace, string Name)? ResolveCoordinates(ExecutionJobRecord job)
    {
        var snapshot = options.CurrentValue;
        var ns = Normalize(job.Spec.Parameters.GetValueOrDefault(KubernetesJobParameterKeys.Namespace))
            ?? Normalize(snapshot.DefaultNamespace)
            ?? KubernetesJobClient.TryReadInClusterNamespace();
        if (string.IsNullOrWhiteSpace(ns))
        {
            return null;
        }

        var name = BuildJobName(job.OperationId);
        return (ns!, name);
    }

    internal static string BuildJobName(string operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);

        var sanitized = SanitizeLabelValue(operationId);
        var candidate = sanitized.StartsWith("honua-", StringComparison.Ordinal)
            ? sanitized
            : $"honua-{sanitized}";
        if (candidate.Length > 63)
        {
            candidate = candidate[..63].TrimEnd('-', '.');
        }

        return candidate;
    }

    internal static string SanitizeLabelValue(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[Math.Min(value.Length, 63)];
        var length = 0;
        foreach (var ch in value)
        {
            if (length == buffer.Length)
            {
                break;
            }

            var lowered = char.ToLowerInvariant(ch);
            if (char.IsAsciiLetterOrDigit(lowered) || lowered == '-' || lowered == '.')
            {
                buffer[length++] = lowered;
            }
            else
            {
                buffer[length++] = '-';
            }
        }

        var trimmed = new string(buffer[..length]).Trim('-', '.');
        return trimmed.Length == 0 ? "job" : trimmed;
    }

    private static string? BuildFailureMessage(
        KubernetesJobStatusSnapshot snapshot,
        IReadOnlyList<KubernetesPodStatusSnapshot> pods)
    {
        var podDetail = pods
            .Select(p => p.ContainerTerminationMessage
                ?? p.ContainerTerminationReason
                ?? p.Message
                ?? p.Reason)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        var detail = podDetail
            ?? snapshot.TerminalMessage
            ?? snapshot.TerminalReason;
        return detail is null
            ? "Kubernetes Job failed."
            : $"Kubernetes Job failed: {detail}";
    }

    private static double? ProjectPercentComplete(ExecutionJobStatus status, double? current) => status switch
    {
        ExecutionJobStatus.Succeeded => 100d,
        ExecutionJobStatus.Cancelled => current,
        ExecutionJobStatus.Failed => current,
        _ => current
    };

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Dictionary<string, string>? ParseMap(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return null;
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in encoded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0 || separator == pair.Length - 1)
            {
                continue;
            }

            var key = pair[..separator].Trim();
            var value = pair[(separator + 1)..].Trim();
            if (!string.IsNullOrEmpty(key))
            {
                result[key] = value;
            }
        }

        return result.Count == 0 ? null : result;
    }

    private static List<string>? ParseList(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return null;
        }

        var result = new List<string>();
        foreach (var entry in encoded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrEmpty(entry))
            {
                result.Add(entry);
            }
        }

        return result.Count == 0 ? null : result;
    }

    private static partial class Log
    {
        [LoggerMessage(9060, LogLevel.Warning,
            "Kubernetes execution backend could not resolve a container image for execution job {OperationId}.")]
        public static partial void MissingImage(ILogger logger, string operationId);

        [LoggerMessage(9061, LogLevel.Information,
            "Kubernetes Job {Namespace}/{Name} for execution job {OperationId} already exists; treating submission as idempotent.")]
        public static partial void JobAlreadyExists(ILogger logger, string operationId, string @namespace, string name);

        [LoggerMessage(9062, LogLevel.Warning,
            "Kubernetes Job submission failed for execution job {OperationId} ({Namespace}/{Name})")]
        public static partial void SubmissionFailed(ILogger logger, string operationId, string @namespace, string name, Exception exception);

        [LoggerMessage(9063, LogLevel.Warning,
            "Kubernetes Job observation failed for execution job {OperationId} ({Namespace}/{Name})")]
        public static partial void ObservationFailed(ILogger logger, string operationId, string @namespace, string name, Exception exception);

        [LoggerMessage(9064, LogLevel.Warning,
            "Kubernetes Job cancellation failed for execution job {OperationId} ({Namespace}/{Name})")]
        public static partial void CancellationFailed(ILogger logger, string operationId, string @namespace, string name, Exception exception);
    }
}

/// <summary>
/// Parameter keys consumed by the Kubernetes execution backend from
/// <see cref="ExecutionJobSpec.Parameters"/>. Keys are chosen so the spec stays
/// provider-agnostic at the canonical layer while giving operators full control
/// over cluster-specific placement when they do target Kubernetes directly.
/// </summary>
internal static class KubernetesJobParameterKeys
{
    public const string Namespace = "k8s.namespace";
    public const string Image = "k8s.image";
    public const string ImagePullPolicy = "k8s.image_pull_policy";
    public const string ImagePullSecrets = "k8s.image_pull_secrets";
    public const string NodeSelector = "k8s.node_selector";
    public const string ServiceAccount = "k8s.service_account";
    public const string CpuRequest = "k8s.cpu_request";
    public const string CpuLimit = "k8s.cpu_limit";
    public const string MemoryRequest = "k8s.memory_request";
    public const string MemoryLimit = "k8s.memory_limit";
    public const string ActiveDeadlineSeconds = "k8s.active_deadline_seconds";
    public const string TtlSecondsAfterFinished = "k8s.ttl_seconds_after_finished";
    public const string EnvironmentPrefix = "k8s.env.";
}
