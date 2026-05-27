// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.WorkflowPackages.Domain;

/// <summary>
/// Mutable workflow package draft metadata and graph.
/// </summary>
public sealed record WorkflowPackage
{
    /// <summary>
    /// Stable package identifier.
    /// </summary>
    public required string PackageId { get; init; }

    /// <summary>
    /// Human-readable package name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Optional package description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Logical namespace for Console authoring.
    /// </summary>
    public string? Namespace { get; init; }

    /// <summary>
    /// Current mutable draft graph.
    /// </summary>
    public required WorkflowGraph Graph { get; init; }

    /// <summary>
    /// Latest immutable version number, if one has been created.
    /// </summary>
    public int? LatestVersion { get; init; }

    /// <summary>
    /// UTC timestamp when the package draft was first created.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// UTC timestamp when the package draft was most recently updated.
    /// </summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// Actor that created the package draft.
    /// </summary>
    public string? CreatedBy { get; init; }

    /// <summary>
    /// Actor that most recently updated the package draft.
    /// </summary>
    public string? UpdatedBy { get; init; }

    /// <summary>
    /// Opaque metadata carried with the package draft.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Immutable package version snapshot.
/// </summary>
public sealed record WorkflowPackageVersion
{
    /// <summary>
    /// Stable package identifier.
    /// </summary>
    public required string PackageId { get; init; }

    /// <summary>
    /// Monotonic version number within the package.
    /// </summary>
    public required int Version { get; init; }

    /// <summary>
    /// Workflow package schema version.
    /// </summary>
    public required string SchemaVersion { get; init; }

    /// <summary>
    /// Stable content hash for the package graph.
    /// </summary>
    public required string PackageHash { get; init; }

    /// <summary>
    /// Immutable graph snapshot.
    /// </summary>
    public required WorkflowGraph Graph { get; init; }

    /// <summary>
    /// Validation result captured when the version was created.
    /// </summary>
    public required WorkflowPackageValidationResult Validation { get; init; }

    /// <summary>
    /// UTC timestamp when the version was created.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Actor that created the immutable version.
    /// </summary>
    public string? CreatedBy { get; init; }
}

/// <summary>
/// Workflow graph containing nodes and directed edges.
/// </summary>
public sealed record WorkflowGraph
{
    /// <summary>
    /// Workflow package schema version for this graph.
    /// </summary>
    public string SchemaVersion { get; init; } = "workflow-package.v1";

    /// <summary>
    /// Nodes in the package DAG.
    /// </summary>
    public required IReadOnlyList<WorkflowNode> Nodes { get; init; }

    /// <summary>
    /// Directed edges between graph nodes.
    /// </summary>
    public IReadOnlyList<WorkflowEdge> Edges { get; init; } = [];

    /// <summary>
    /// Optional schedule hints attached to the graph draft.
    /// </summary>
    public WorkflowSchedule? Schedule { get; init; }

    /// <summary>
    /// Optional worker profile requested by the package author.
    /// </summary>
    public string? WorkerProfile { get; init; }

    /// <summary>
    /// Opaque editor metadata. Runtime validation ignores these values.
    /// </summary>
    public IReadOnlyDictionary<string, string> EditorMetadata { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Node instance inside a workflow graph.
/// </summary>
public sealed record WorkflowNode
{
    /// <summary>
    /// Node identifier unique within the graph.
    /// </summary>
    public required string NodeId { get; init; }

    /// <summary>
    /// Node type identifier from the node registry.
    /// </summary>
    public required string NodeTypeId { get; init; }

    /// <summary>
    /// Parameter values represented as strings for AOT-safe transport.
    /// </summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Optional per-node worker profile override.
    /// </summary>
    public string? WorkerProfile { get; init; }

    /// <summary>
    /// Opaque node metadata.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Directed edge between workflow graph nodes.
/// </summary>
public sealed record WorkflowEdge
{
    /// <summary>
    /// Source node identifier.
    /// </summary>
    public required string SourceNodeId { get; init; }

    /// <summary>
    /// Target node identifier.
    /// </summary>
    public required string TargetNodeId { get; init; }

    /// <summary>
    /// Edge kind.
    /// </summary>
    public WorkflowEdgeKind Kind { get; init; } = WorkflowEdgeKind.Data;

    /// <summary>
    /// Optional source output port.
    /// </summary>
    public string? SourcePort { get; init; }

    /// <summary>
    /// Optional target input or parameter key.
    /// </summary>
    public string? TargetPort { get; init; }
}

/// <summary>
/// Schedule declaration attached to a workflow package or publication.
/// </summary>
public sealed record WorkflowSchedule
{
    /// <summary>
    /// Five-field cron expression.
    /// </summary>
    public required string CronExpression { get; init; }

    /// <summary>
    /// IANA or platform time zone identifier. Defaults to UTC when unset.
    /// </summary>
    public string? TimeZone { get; init; }

    /// <summary>
    /// Whether the schedule is enabled.
    /// </summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// Immutable publication of a workflow package version to a target.
/// </summary>
public sealed record WorkflowPublication
{
    /// <summary>
    /// Stable publication identifier.
    /// </summary>
    public required string PublicationId { get; init; }

    /// <summary>
    /// Package identifier.
    /// </summary>
    public required string PackageId { get; init; }

    /// <summary>
    /// Package version.
    /// </summary>
    public required int PackageVersion { get; init; }

    /// <summary>
    /// Package content hash.
    /// </summary>
    public required string PackageHash { get; init; }

    /// <summary>
    /// Publication target.
    /// </summary>
    public required WorkflowPublicationTarget Target { get; init; }

    /// <summary>
    /// Publication lifecycle status.
    /// </summary>
    public WorkflowPublicationStatus Status { get; init; } = WorkflowPublicationStatus.Active;

    /// <summary>
    /// Process identifier when the target is <see cref="WorkflowPublicationTarget.ProcessEndpoint"/>.
    /// </summary>
    public string? ProcessId { get; init; }

    /// <summary>
    /// Schedule declaration when the target is <see cref="WorkflowPublicationTarget.Schedule"/>.
    /// </summary>
    public WorkflowSchedule? Schedule { get; init; }

    /// <summary>
    /// Workflow definition identifier created for schedule-backed publications.
    /// </summary>
    public string? WorkflowDefinitionId { get; init; }

    /// <summary>
    /// Endpoint path clients can call to run this publication.
    /// </summary>
    public string? EndpointPath { get; init; }

    /// <summary>
    /// Validation and target eligibility result captured at publication time.
    /// </summary>
    public required WorkflowPackageValidationResult Eligibility { get; init; }

    /// <summary>
    /// UTC timestamp when the publication was created.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Actor that created the publication.
    /// </summary>
    public string? CreatedBy { get; init; }

    /// <summary>
    /// Metadata stamped onto jobs, runs, logs, and provenance records.
    /// </summary>
    public IReadOnlyDictionary<string, string> Provenance { get; init; } = new Dictionary<string, string>();
}
