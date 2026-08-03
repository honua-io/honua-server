// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Geoprocessing;

/// <summary>Configured health of the local geoprocessing execution lane.</summary>
internal enum GpLocalCapacityState
{
    Healthy,
    Pressured,
    Unavailable,
}

/// <summary>
/// Provider-neutral local/offload policy for ordinary geoprocessing jobs. Workload-specific
/// compatibility and capacity declarations live on each ControlPlane execution workload.
/// </summary>
internal sealed class GpWorkloadPlacementOptions
{
    public const string SectionName = "Geoprocessing:WorkloadPlacement";

    [Required]
    public string PolicyVersion { get; set; } = "gp-placement-v1";

    /// <summary>Allow the in-process queue and local process-pool lanes.</summary>
    public bool LocalExecutionEnabled { get; set; } = true;

    /// <summary>Allow Kubernetes, AWS Batch, Azure Batch, and future remote lanes.</summary>
    public bool RemoteExecutionEnabled { get; set; } = true;

    /// <summary>Require every ordinary non-raster job to run in a remote isolation lane.</summary>
    public bool ForceRemoteIsolation { get; set; }

    /// <summary>Permit remote execution when a preferred local lane is unavailable or pressured.</summary>
    public bool AllowRemoteFallback { get; set; } = true;

    /// <summary>Permit local execution when a preferred (not forced) remote lane is unavailable.</summary>
    public bool AllowLocalFallback { get; set; } = true;

    /// <summary>Permit a pressured local workload as the final fallback.</summary>
    public bool AllowPressuredLocalFallback { get; set; }

    /// <summary>Operator-supplied aggregate local-capacity snapshot.</summary>
    public GpLocalCapacityState LocalCapacity { get; set; } = GpLocalCapacityState.Healthy;

    /// <summary>Jobs above this vCPU request prefer burst/offload execution.</summary>
    [Range(1, int.MaxValue)]
    public int MaxLowLatencyLocalVcpus { get; set; } = 4;

    /// <summary>Jobs above this memory request prefer burst/offload execution.</summary>
    [Range(1, int.MaxValue)]
    public int MaxLowLatencyLocalMemoryMib { get; set; } = 8192;

    /// <summary>Jobs above this GPU request prefer burst/offload execution.</summary>
    [Range(0, int.MaxValue)]
    public int MaxLowLatencyLocalGpuCount { get; set; }

    /// <summary>Jobs above this timeout prefer burst/offload execution.</summary>
    [Range(1, int.MaxValue)]
    public int MaxLowLatencyLocalTimeoutSeconds { get; set; } = 3600;

    /// <summary>Jobs above this retry request prefer burst/offload execution.</summary>
    [Range(1, int.MaxValue)]
    public int MaxLowLatencyLocalRetryAttempts { get; set; } = 3;

    /// <summary>Jobs above this scratch-storage request prefer burst/offload execution.</summary>
    [Range(1, int.MaxValue)]
    public int MaxLowLatencyLocalEphemeralGib { get; set; } = 100;

    public bool HasDefinedEnumValues() => Enum.IsDefined(LocalCapacity);
}

/// <summary>Stable workload and request parameter names used by the placement planner.</summary>
internal static class GpWorkloadPlacementParameterKeys
{
    // Per-job policy requests.
    public const string Mode = "gp.placement.mode";
    public const string Backend = "gp.placement.backend";
    public const string Affinity = "gp.placement.affinity";

    // Workload declarations.
    public const string Enabled = "placement.enabled";
    public const string ExecutionClass = "placement.class";
    public const string Capacity = "placement.capacity";
    public const string RuntimeProfiles = "placement.runtime_profiles";
    public const string Architectures = "placement.architectures";
    public const string Affinities = "placement.affinities";
    public const string Priority = "placement.priority";
    public const string MaxVcpus = "placement.max_vcpus";
    public const string MaxMemoryMib = "placement.max_memory_mib";
    public const string MaxGpuCount = "placement.max_gpu_count";
    public const string MaxTimeoutSeconds = "placement.max_timeout_seconds";
    public const string MaxRetryAttempts = "placement.max_retry_attempts";
    public const string MaxEphemeralGib = "placement.max_ephemeral_gib";
}
