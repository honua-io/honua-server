// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.WorkflowPackages.Domain;

public sealed record WorkflowPackage
{
    public required string PackageId { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public string? Namespace { get; init; }

    public required WorkflowGraph Graph { get; init; }

    public int? LatestVersion { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public string? CreatedBy { get; init; }

    public string? UpdatedBy { get; init; }

    public IReadOnlyDictionary<string, string> Metadata { get; init => field = value ?? new Dictionary<string, string>(); } = new Dictionary<string, string>();
}

public sealed record WorkflowPackageVersion
{
    public required string PackageId { get; init; }

    public required int Version { get; init; }

    public required string SchemaVersion { get; init; }

    public required string PackageHash { get; init; }

    public required WorkflowGraph Graph { get; init; }

    public required WorkflowPackageValidationResult Validation { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public string? CreatedBy { get; init; }
}

public sealed record WorkflowGraph
{
    public string SchemaVersion { get; init; } = "workflow-package.v1";

    public required IReadOnlyList<WorkflowNode> Nodes { get; init; }

    public IReadOnlyList<WorkflowEdge> Edges { get; init => field = value ?? []; } = [];

    public WorkflowSchedule? Schedule { get; init; }

    public string? WorkerProfile { get; init; }

    // Editor metadata is ignored by runtime validation, and an omitted value reads as empty because source-generated JSON skips property initializers.
    public IReadOnlyDictionary<string, string> EditorMetadata { get; init => field = value ?? new Dictionary<string, string>(); } = new Dictionary<string, string>();
}

public sealed record WorkflowNode
{
    public required string NodeId { get; init; }

    public required string NodeTypeId { get; init; }

    public IReadOnlyDictionary<string, string> Parameters { get; init => field = value ?? new Dictionary<string, string>(); } = new Dictionary<string, string>();

    public string? WorkerProfile { get; init; }

    public IReadOnlyDictionary<string, string> Metadata { get; init => field = value ?? new Dictionary<string, string>(); } = new Dictionary<string, string>();
}

public sealed record WorkflowEdge
{
    public required string SourceNodeId { get; init; }

    public required string TargetNodeId { get; init; }

    public WorkflowEdgeKind Kind { get; init; } = WorkflowEdgeKind.Data;

    public string? SourcePort { get; init; }

    public string? TargetPort { get; init; }
}

public sealed record WorkflowSchedule
{
    public required string CronExpression { get; init; }

    public string? TimeZone { get; init; }

    public bool Enabled { get; init; } = true;
}

public sealed record WorkflowPublication
{
    public required string PublicationId { get; init; }

    public required string PackageId { get; init; }

    public required int PackageVersion { get; init; }

    public required string PackageHash { get; init; }

    public required WorkflowPublicationTarget Target { get; init; }

    public WorkflowPublicationStatus Status { get; init; } = WorkflowPublicationStatus.Active;

    public string? ProcessId { get; init; }

    public WorkflowSchedule? Schedule { get; init; }

    public string? WorkflowDefinitionId { get; init; }

    public string? EndpointPath { get; init; }

    public required WorkflowPackageValidationResult Eligibility { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public string? CreatedBy { get; init; }

    public IReadOnlyDictionary<string, string> Provenance { get; init; } = new Dictionary<string, string>();
}
