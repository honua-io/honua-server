// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Features.Infrastructure.ControlPlane;

/// <summary>
/// Configuration-backed catalogs for control-plane deploy targets and workload definitions.
/// </summary>
internal sealed class ControlPlaneOptions
{
    /// <summary>
    /// Configuration section name for control-plane catalogs.
    /// </summary>
    public const string SectionName = "ControlPlane";

    /// <summary>
    /// Stable deploy target catalog entries.
    /// </summary>
    public List<DeployTargetOptions> DeployTargets { get; set; } = [];

    /// <summary>
    /// Stable execution workload catalog entries.
    /// </summary>
    public List<ExecutionWorkloadOptions> ExecutionWorkloads { get; set; } = [];

    /// <summary>
    /// Named telemetry query connections used for deploy health gating.
    /// </summary>
    public List<DeployTelemetryConnectionOptions> TelemetryConnections { get; set; } = [];
}

/// <summary>
/// Configuration model for a stable deploy target.
/// </summary>
internal sealed class DeployTargetOptions
{
    public string TargetId { get; set; } = string.Empty;

    public DeployTargetKind TargetKind { get; set; } = DeployTargetKind.Kubernetes;

    public string Backend { get; set; } = string.Empty;

    public string Environment { get; set; } = string.Empty;

    public string TargetName { get; set; } = string.Empty;

    public string? ArtifactReference { get; set; }

    public string? RuntimeProfile { get; set; }

    public bool RequiresApproval { get; set; }

    public bool RequiresOutOfBandMigrations { get; set; }

    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Configuration model for a telemetry query connection used by deploy rollback gates.
/// </summary>
internal sealed class DeployTelemetryConnectionOptions
{
    public string ConnectionId { get; set; } = string.Empty;

    public string Provider { get; set; } = "prometheus";

    public string BaseUrl { get; set; } = string.Empty;

    public string QueryPath { get; set; } = "/api/v1/query";

    public string? AuthHeaderName { get; set; }

    public string? AuthHeaderValue { get; set; }

    public int TimeoutSeconds { get; set; } = 10;
}

/// <summary>
/// Configuration model for a stable execution workload definition.
/// </summary>
internal sealed class ExecutionWorkloadOptions
{
    public string WorkloadId { get; set; } = string.Empty;

    public BatchComputeTargetKind TargetKind { get; set; } = BatchComputeTargetKind.KubernetesJob;

    public string Backend { get; set; } = string.Empty;

    public ExecutionJobKind Kind { get; set; } = ExecutionJobKind.Geoprocessing;

    public string WorkloadName { get; set; } = string.Empty;

    public string? ArtifactReference { get; set; }

    public string? RuntimeProfile { get; set; }

    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.Ordinal);
}
