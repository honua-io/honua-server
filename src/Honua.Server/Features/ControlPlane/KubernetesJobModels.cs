// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.ControlPlane;

/// <summary>
/// Normalized view of a Kubernetes Job's lifecycle-relevant fields, derived from the
/// <c>batch/v1</c> <c>Job</c> API response by the adapter client.
/// </summary>
internal sealed record KubernetesJobStatusSnapshot
{
    /// <summary>
    /// Server-assigned Job UID.
    /// </summary>
    public string? Uid { get; init; }

    /// <summary>
    /// Job namespace.
    /// </summary>
    public string? Namespace { get; init; }

    /// <summary>
    /// Job name.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Number of actively running pods.
    /// </summary>
    public int Active { get; init; }

    /// <summary>
    /// Number of pods that completed successfully.
    /// </summary>
    public int Succeeded { get; init; }

    /// <summary>
    /// Number of pods that terminated with failure.
    /// </summary>
    public int Failed { get; init; }

    /// <summary>
    /// Whether the Job declared a <c>Complete</c> condition with status True.
    /// </summary>
    public bool CompleteCondition { get; init; }

    /// <summary>
    /// Whether the Job declared a <c>Failed</c> condition with status True.
    /// </summary>
    public bool FailedCondition { get; init; }

    /// <summary>
    /// Reason attached to the terminal condition when available.
    /// </summary>
    public string? TerminalReason { get; init; }

    /// <summary>
    /// Message attached to the terminal condition when available.
    /// </summary>
    public string? TerminalMessage { get; init; }
}

/// <summary>
/// Lifecycle-relevant projection of a Kubernetes Pod associated with a Job.
/// </summary>
internal sealed record KubernetesPodStatusSnapshot
{
    /// <summary>
    /// Pod name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Pod lifecycle phase: Pending, Running, Succeeded, Failed, Unknown.
    /// </summary>
    public string? Phase { get; init; }

    /// <summary>
    /// Pod-level reason when phase is Failed.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Pod-level message when phase is Failed.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// Termination reason for the primary container, when available.
    /// </summary>
    public string? ContainerTerminationReason { get; init; }

    /// <summary>
    /// Termination message for the primary container, when available.
    /// </summary>
    public string? ContainerTerminationMessage { get; init; }

    /// <summary>
    /// Container exit code when the primary container has terminated.
    /// </summary>
    public int? ContainerExitCode { get; init; }
}

/// <summary>
/// Declarative inputs consumed by <see cref="IKubernetesJobClient.CreateJobAsync"/> to
/// synthesize a minimal <c>batch/v1</c> Kubernetes Job manifest.
/// </summary>
internal sealed record KubernetesJobManifest
{
    /// <summary>
    /// Target namespace. Required.
    /// </summary>
    public required string Namespace { get; init; }

    /// <summary>
    /// Job name used as the stable provider identifier. Required.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Container image reference. Required.
    /// </summary>
    public required string Image { get; init; }

    /// <summary>
    /// Labels applied to the Job and pod template.
    /// </summary>
    public IReadOnlyDictionary<string, string> Labels { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Annotations applied to the Job metadata.
    /// </summary>
    public IReadOnlyDictionary<string, string> Annotations { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Optional container CPU request (for example <c>500m</c>).
    /// </summary>
    public string? CpuRequest { get; init; }

    /// <summary>
    /// Optional container CPU limit.
    /// </summary>
    public string? CpuLimit { get; init; }

    /// <summary>
    /// Optional container memory request (for example <c>4Gi</c>).
    /// </summary>
    public string? MemoryRequest { get; init; }

    /// <summary>
    /// Optional container memory limit.
    /// </summary>
    public string? MemoryLimit { get; init; }

    /// <summary>
    /// Optional node selector key/value pairs.
    /// </summary>
    public IReadOnlyDictionary<string, string> NodeSelector { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Optional service account used by the pod.
    /// </summary>
    public string? ServiceAccount { get; init; }

    /// <summary>
    /// Optional container image pull policy (Always, Never, IfNotPresent).
    /// </summary>
    public string? ImagePullPolicy { get; init; }

    /// <summary>
    /// Optional container image pull secret names.
    /// </summary>
    public IReadOnlyList<string> ImagePullSecrets { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Optional pod active deadline in seconds (Kubernetes <c>activeDeadlineSeconds</c>).
    /// </summary>
    public int? ActiveDeadlineSeconds { get; init; }

    /// <summary>
    /// Optional TTL in seconds after the Job completes (Kubernetes <c>ttlSecondsAfterFinished</c>).
    /// </summary>
    public int? TtlSecondsAfterFinished { get; init; }

    /// <summary>
    /// Optional environment variables to inject into the primary container.
    /// </summary>
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
