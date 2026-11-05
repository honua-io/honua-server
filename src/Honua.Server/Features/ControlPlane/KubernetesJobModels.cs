// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.ControlPlane;

internal sealed record KubernetesJobStatusSnapshot
{
    public string? Uid { get; init; }

    public string? Namespace { get; init; }

    public string? Name { get; init; }

    public int Active { get; init; }

    public int Succeeded { get; init; }

    public int Failed { get; init; }

    public bool CompleteCondition { get; init; }

    public bool FailedCondition { get; init; }

    public string? TerminalReason { get; init; }

    public string? TerminalMessage { get; init; }
}

internal sealed record KubernetesPodStatusSnapshot
{
    public required string Name { get; init; }

    public string? Phase { get; init; }

    public string? Reason { get; init; }

    public string? Message { get; init; }

    public string? ContainerTerminationReason { get; init; }

    public string? ContainerTerminationMessage { get; init; }

    public int? ContainerExitCode { get; init; }
}

internal sealed record KubernetesJobManifest
{
    public required string Namespace { get; init; }

    public required string Name { get; init; }

    public required string Image { get; init; }

    public IReadOnlyDictionary<string, string> Labels { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> Annotations { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public string? CpuRequest { get; init; }

    public string? CpuLimit { get; init; }

    public string? MemoryRequest { get; init; }

    public string? MemoryLimit { get; init; }

    public IReadOnlyDictionary<string, string> NodeSelector { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public string? ServiceAccount { get; init; }

    public string? ImagePullPolicy { get; init; }

    public IReadOnlyList<string> ImagePullSecrets { get; init; } = Array.Empty<string>();

    public int? ActiveDeadlineSeconds { get; init; }

    public int? TtlSecondsAfterFinished { get; init; }

    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    // These environment entries carry Secret or ConfigMap reference names only.
    public IReadOnlyDictionary<string, KubernetesEnvKeyReference> EnvironmentReferences { get; init; } =
        new Dictionary<string, KubernetesEnvKeyReference>(StringComparer.Ordinal);

    public IReadOnlyList<KubernetesEnvFromSource> EnvFrom { get; init; } = Array.Empty<KubernetesEnvFromSource>();
}
