// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Options;

namespace Honua.ControlPlane;

/// <summary>
/// Batch compute adapter that executes canonical execution jobs on a Kubernetes
/// cluster by translating the <see cref="ExecutionJobRecord"/> into a
/// <c>batch/v1</c> Job and projecting Job + Pod lifecycle back onto
/// <see cref="ExecutionJobStatus"/>. The adapter keeps Jobs at
/// <c>backoffLimit: 0</c> so a failed Pod surfaces terminally rather than being
/// silently retried inside the cluster. Canonical retry orchestration remains in
/// the shared <see cref="ExecutionJobReconciler"/>; the adapter uses
/// attempt-specific Job names so each retry starts a fresh Kubernetes Job rather
/// than colliding with the failed attempt's retained provider object.
/// </summary>
internal sealed partial class KubernetesJobBatchComputeBackend(
    IKubernetesJobClient jobClient,
    IOptionsMonitor<KubernetesExecutionOptions> options,
    ILogger<KubernetesJobBatchComputeBackend> logger) : IBatchComputeBackend
{
    internal const string BackendId = "honua-kubernetes-job";

    private const string OperationIdLabel = "honua.io/operation-id";
    // Mirrors the raw OperationId into an annotation when label sanitization drops or
    // rewrites characters so operators can still trace a Job back to its canonical
    // OperationId value (labels cap at 63 chars and allow only
    // [a-z0-9._-], while annotations accept any UTF-8).
    private const string OperationIdAnnotation = "honua.io/operation-id-original";
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
            ControlPlaneTelemetry.Activities.ExecutionStart, "start", job);

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
        // Pin the resolved namespace onto the durable record so subsequent observe, cancel,
        // and retry paths do not re-resolve it against potentially-changed current options.
        // Without this, mutating ControlPlane:Kubernetes:DefaultNamespace (or restarting with
        // a different default) causes lifecycle calls to query the wrong namespace and
        // surface false NotFound outcomes that would incorrectly fail or cancel live jobs.
        var resolvedParameters = BuildResolvedSubmissionParameters(job, manifest);

        try
        {
            var result = await jobClient.CreateJobAsync(manifest, cancellationToken).ConfigureAwait(false);
            if (result.AlreadyExists)
            {
                Log.JobAlreadyExists(logger, job.OperationId, manifest.Namespace, manifest.Name);
                ControlPlaneTelemetry.RecordExecutionRequest(job, "start", "already_exists");

                // Resume the existing Job through the shared observe/retry path: always
                // return a nonterminal Queued (with the provider marker preserved) so the
                // reconciler's next cycle routes the record through ObserveJobAsync via
                // HasSubmittedProviderMarker. Terminalizing here would short-circuit
                // ExecutionJobReconciler.TryRequeueFailedRemoteJobAsync and skip the
                // configured retry policy when the pre-existing Job is already Failed.
                // Mirrors AzureBatchComputeBackend, whose 409 path also resumes in Queued.
                //
                // 409 has already proved the Job name exists on the cluster, so a transient
                // transport/config failure on the follow-up GET must not flip the durable
                // record to Failed via the outer catch. Keep the lookup best-effort; if it
                // fails, log and fall back to the manifest name (reconciliation resolves
                // the provider Uid on the next observe cycle).
                string? existingUid = null;
                try
                {
                    var existing = await jobClient.GetJobAsync(manifest.Namespace, manifest.Name, cancellationToken)
                        .ConfigureAwait(false);
                    existingUid = existing.Snapshot?.Uid;
                }
                catch (Exception lookupEx) when (IsTransportOrConfigFailure(lookupEx))
                {
                    Log.ConflictLookupFailed(logger, job.OperationId, manifest.Namespace, manifest.Name, lookupEx);
                }

                return new BatchComputeSubmissionResult
                {
                    Status = ExecutionJobStatus.Queued,
                    ProviderOperationId = existingUid ?? job.ProviderOperationId ?? manifest.Name,
                    Message = "Kubernetes Job already exists; resuming observation on the existing Job.",
                    ResolvedParameters = resolvedParameters
                };
            }

            ControlPlaneTelemetry.RecordExecutionRequest(job, "start", "submitted");
            // Mirror AzureBatchComputeBackend: fresh submissions bump the shared
            // honua.execution.job.submitted counter; idempotent 409 resumes must not.
            ControlPlaneTelemetry.RecordExecutionSubmission(job);
            return new BatchComputeSubmissionResult
            {
                Status = ExecutionJobStatus.Provisioning,
                ProviderOperationId = result.Snapshot?.Uid ?? manifest.Name,
                Message = $"Submitted Kubernetes Job {manifest.Namespace}/{manifest.Name}.",
                ResolvedParameters = resolvedParameters
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
            ControlPlaneTelemetry.Activities.ExecutionObserve, "observe", job);

        var (namespaceName, jobName) = ResolveCoordinates(job);

        try
        {
            var fetch = await jobClient.GetJobAsync(namespaceName, jobName, cancellationToken)
                .ConfigureAwait(false);
            if (fetch.NotFound || fetch.Snapshot == null)
            {
                ControlPlaneTelemetry.RecordExecutionRequest(job, "observe", "not_found");
                return BuildObservationForMissingJob(job);
            }

            var snapshot = fetch.Snapshot;
            var status = MapStatus(snapshot, job.Status);

            string? message = null;
            if (status == ExecutionJobStatus.Failed)
            {
                var pods = await jobClient
                    .ListPodsAsync(
                        namespaceName,
                        BuildJobPodSelector(jobName),
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
            Log.ObservationFailed(logger, job.OperationId, namespaceName, jobName, ex);
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
            ControlPlaneTelemetry.Activities.ExecutionCancel, "cancel", job);

        var (namespaceName, jobName) = ResolveCoordinates(job);

        try
        {
            // Observe the provider state before requesting deletion so a Job that
            // already reached Succeeded/Failed (for example, the pod finished between
            // the reconciler sweep and this cancel hop) is not overwritten with
            // Cancelled by the subsequent authoritative store write. Matches the
            // terminal-preservation rule used by BuildObservationForMissingJob and
            // the local backend's ObserveAsync-backed cancel.
            var terminal = await TryObserveTerminalBeforeCancelAsync(
                    job, namespaceName, jobName, cancellationToken)
                .ConfigureAwait(false);
            if (terminal != null)
            {
                ControlPlaneTelemetry.RecordExecutionRequest(
                    job, "cancel", terminal.Status.ToString().ToLowerInvariant() + "_race");
                return terminal;
            }

            var result = await jobClient.DeleteJobAsync(namespaceName, jobName, cancellationToken)
                .ConfigureAwait(false);

            // A missing provider Job must not be reclassified as Cancelled over the
            // durable record: the normal observe path routes the same condition
            // through BuildObservationForMissingJob (preserves any already-terminal
            // local state, otherwise Failed because the Job was lost before it
            // could reach a terminal state on the cluster). Mirror that here so the
            // cancel-path persists the same outcome as ObserveAsync.
            if (result.NotFound)
            {
                var missing = BuildObservationForMissingJob(job);
                ControlPlaneTelemetry.RecordExecutionRequest(job, "cancel", "not_found");
                return missing;
            }

            // The Job may have transitioned to Succeeded/Failed in the window between
            // the pre-delete GET and this DELETE. The Kubernetes API returns the Job
            // object at deletion time, so use its status conditions (when provided) to
            // preserve any terminal outcome the race missed.
            if (result.Snapshot is { } deleteSnapshot)
            {
                var postDelete = await BuildTerminalObservationFromSnapshotAsync(
                        job, namespaceName, jobName, deleteSnapshot, cancellationToken)
                    .ConfigureAwait(false);
                if (postDelete != null)
                {
                    ControlPlaneTelemetry.RecordExecutionRequest(
                        job, "cancel", postDelete.Status.ToString().ToLowerInvariant() + "_race");
                    return postDelete;
                }
            }

            ControlPlaneTelemetry.RecordExecutionRequest(job, "cancel", "deleted");
            return new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Cancelled,
                ProviderOperationId = job.ProviderOperationId,
                PercentComplete = job.PercentComplete,
                Message = $"Requested cascade deletion of Kubernetes Job {namespaceName}/{jobName}."
            };
        }
        catch (Exception ex) when (IsTransportOrConfigFailure(ex))
        {
            Log.CancellationFailed(logger, job.OperationId, namespaceName, jobName, ex);
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

    private async Task<BatchComputeObservation?> TryObserveTerminalBeforeCancelAsync(
        ExecutionJobRecord job,
        string namespaceName,
        string jobName,
        CancellationToken cancellationToken)
    {
        var fetch = await jobClient.GetJobAsync(namespaceName, jobName, cancellationToken)
            .ConfigureAwait(false);
        if (fetch.NotFound || fetch.Snapshot == null)
        {
            return null;
        }

        return await BuildTerminalObservationFromSnapshotAsync(
                job, namespaceName, jobName, fetch.Snapshot, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<BatchComputeObservation?> BuildTerminalObservationFromSnapshotAsync(
        ExecutionJobRecord job,
        string namespaceName,
        string jobName,
        KubernetesJobStatusSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var observed = MapStatus(snapshot, job.Status);
        if (observed is not (ExecutionJobStatus.Succeeded or ExecutionJobStatus.Failed))
        {
            return null;
        }

        string? message;
        if (observed == ExecutionJobStatus.Failed)
        {
            var pods = await jobClient
                .ListPodsAsync(
                    namespaceName,
                    BuildJobPodSelector(snapshot.Name ?? jobName),
                    cancellationToken)
                .ConfigureAwait(false);
            message = BuildFailureMessage(snapshot, pods);
        }
        else
        {
            message = "Kubernetes Job completed before cancellation took effect.";
        }

        return new BatchComputeObservation
        {
            Status = observed,
            ProviderOperationId = snapshot.Uid ?? job.ProviderOperationId,
            PercentComplete = ProjectPercentComplete(observed, job.PercentComplete),
            Message = message ?? job.CurrentPhase
        };
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

        var ns = ResolveNamespace(job, snapshot);
        var attemptNumber = ResolveSubmissionAttempt(job);
        var name = BuildJobName(job.OperationId, attemptNumber);

        var operationIdLabel = BuildOperationIdLabelValue(job.OperationId);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ManagedByLabel] = ManagedByValue,
            [OperationIdLabel] = operationIdLabel,
            [WorkloadKindLabel] = job.Spec.Kind.ToString().ToLowerInvariant()
        };
        if (!string.IsNullOrWhiteSpace(job.Spec.WorkloadName))
        {
            labels[WorkloadLabel] = SanitizeLabelValue(job.Spec.WorkloadName);
        }

        var annotations = new Dictionary<string, string>(StringComparer.Ordinal);
        // Preserve the original OperationId whenever sanitization changed it so
        // operators can still join Kubernetes objects to canonical records.
        if (!string.Equals(operationIdLabel, job.OperationId, StringComparison.Ordinal))
        {
            annotations[OperationIdAnnotation] = job.OperationId;
        }

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
            return Math.Max(parsed, MinimumTtlSecondsAfterFinished);
        }

        var configured = snapshot.DefaultTtlSecondsAfterFinished;
        return configured.HasValue
            ? Math.Max(configured.Value, MinimumTtlSecondsAfterFinished)
            : null;
    }

    // The reconciler polls every 5 seconds (see ExecutionJobReconcilerBackgroundService).
    // Clamp Kubernetes' ttlSecondsAfterFinished to at least six poll cycles so a Job cannot
    // be garbage-collected before the adapter has a chance to observe its terminal state
    // (and therefore cannot be downgraded from Succeeded to "not found" → Failed).
    internal const int MinimumTtlSecondsAfterFinished = 30;

    private static BatchComputeObservation BuildObservationForMissingJob(ExecutionJobRecord job)
    {
        // Preserve any terminal state we already reached: if a prior observation promoted
        // the job to Succeeded/Failed/Cancelled, a later TTL-driven cleanup must not
        // rewrite that outcome.
        if (IsTerminal(job.Status))
        {
            return new BatchComputeObservation
            {
                Status = job.Status,
                ProviderOperationId = job.ProviderOperationId,
                PercentComplete = job.PercentComplete,
                Message = job.Status == ExecutionJobStatus.Succeeded
                    ? "Kubernetes Job completed and was cleaned up per ttlSecondsAfterFinished."
                    : "Kubernetes Job is no longer present on the cluster."
            };
        }

        // Honor cancellation intent: a repeated or raced cancel where another node (or an
        // operator kubectl action) already deleted the Job must not reclassify a
        // nonterminal record as Failed. Mirrors AzureBatchComputeBackend.MapMissingObservation.
        if (job.CancellationRequestedAt.HasValue)
        {
            return new BatchComputeObservation
            {
                Status = ExecutionJobStatus.Cancelled,
                ProviderOperationId = job.ProviderOperationId,
                PercentComplete = job.PercentComplete,
                Message = "Kubernetes Job is no longer present; cancellation completed."
            };
        }

        return new BatchComputeObservation
        {
            Status = ExecutionJobStatus.Failed,
            ProviderOperationId = job.ProviderOperationId,
            PercentComplete = job.PercentComplete,
            Message = "Kubernetes Job is no longer present on the cluster."
        };
    }

    private static bool IsTerminal(ExecutionJobStatus status) => status switch
    {
        ExecutionJobStatus.Succeeded => true,
        ExecutionJobStatus.Failed => true,
        ExecutionJobStatus.Cancelled => true,
        _ => false
    };

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

    private (string Namespace, string Name) ResolveCoordinates(ExecutionJobRecord job)
    {
        var snapshot = options.CurrentValue;
        var ns = ResolveNamespace(job, snapshot);
        var name = BuildJobName(job.OperationId, ResolveObservedAttempt(job));
        return (ns, name);
    }

    // Captures the namespace the adapter used when creating the Job so the helper can merge
    // it back into spec.Parameters. Only emits an entry when the spec did not already pin
    // it, so operator-set values in spec.Parameters continue to take precedence.
    private static Dictionary<string, string>? BuildResolvedSubmissionParameters(
        ExecutionJobRecord job,
        KubernetesJobManifest manifest)
    {
        if (!string.IsNullOrWhiteSpace(job.Spec.Parameters.GetValueOrDefault(KubernetesJobParameterKeys.Namespace)))
        {
            return null;
        }

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [KubernetesJobParameterKeys.Namespace] = manifest.Namespace
        };
    }

    // Single source of truth for namespace selection so submission, observation, and
    // cancellation agree on the target Job. Fallback chain: spec parameter →
    // configured DefaultNamespace → projected in-cluster namespace (only when
    // InClusterAutoDetect is enabled) → "default". Mirrors the CA-trust decision in
    // KubernetesJobClient.ResolveTrustedCaPath: the projected service-account
    // namespace identifies the local cluster, so a Honua host running inside
    // cluster A that targets cluster B via an explicit ApiServerUrl must not leak
    // the local namespace into requests against the remote cluster.
    private static string ResolveNamespace(ExecutionJobRecord job, KubernetesExecutionOptions snapshot)
        => Normalize(job.Spec.Parameters.GetValueOrDefault(KubernetesJobParameterKeys.Namespace))
            ?? Normalize(snapshot.DefaultNamespace)
            ?? (snapshot.InClusterAutoDetect ? KubernetesJobClient.TryReadInClusterNamespace() : null)
            ?? "default";

    internal static string BuildJobName(string operationId, int attemptNumber = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);

        var sanitized = SanitizeLabelValue(operationId);
        var candidate = sanitized.StartsWith("honua-", StringComparison.Ordinal)
            ? sanitized
            : $"honua-{sanitized}";
        if (attemptNumber > 1)
        {
            var suffix = $"-a{attemptNumber.ToString(CultureInfo.InvariantCulture)}";
            var maxBaseLength = 63 - suffix.Length;
            if (candidate.Length > maxBaseLength)
            {
                candidate = candidate[..maxBaseLength].TrimEnd('-', '.');
            }

            candidate += suffix;
        }
        else if (candidate.Length > 63)
        {
            candidate = candidate[..63].TrimEnd('-', '.');
        }

        return candidate;
    }

    // Kubernetes label values must match DNS-1123 (ascii letter/digit/./-, up to 63
    // chars, trimmed edges). Raw OperationIds can contain characters outside that
    // alphabet (slashes, underscores, longer than 63), so sanitize consistently
    // whenever the value is written as a label or used in a label selector. The
    // original value is preserved in an annotation via BuildManifest when the
    // sanitization mutates it.
    internal static string BuildOperationIdLabelValue(string operationId)
        => SanitizeLabelValue(operationId);

    internal static string BuildOperationIdSelector(string operationId)
        => $"{OperationIdLabel}={BuildOperationIdLabelValue(operationId)}";

    internal static string BuildJobPodSelector(string jobName)
        => $"job-name={jobName}";

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

    private static int ResolveSubmissionAttempt(ExecutionJobRecord job)
        => Math.Max(job.AttemptCount + 1, 1);

    private static int ResolveObservedAttempt(ExecutionJobRecord job)
        => Math.Max(job.AttemptCount, 1);

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

        [LoggerMessage(9065, LogLevel.Warning,
            "Kubernetes Job conflict lookup failed for execution job {OperationId} ({Namespace}/{Name}); resuming observation with manifest name")]
        public static partial void ConflictLookupFailed(ILogger logger, string operationId, string @namespace, string name, Exception exception);
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
