// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Domain;

public enum CoordinatedReleaseStep
{
    Preflight,

    Backup,

    ContainerRollout,

    MetadataAndSchema,

    Smoke,

    Promote,

    Complete,

    RollingBack,

    Failed
}

public enum CoordinatedReleaseStepStatus
{
    Pending,

    AwaitingApproval,

    Running,

    Succeeded,

    Failed,

    RolledBack,

    RollbackRequested
}

public enum CoordinatedReleaseArtifactKind
{
    ContainerImage,

    DatabaseSchema,

    Metadata
}

public sealed record CoordinatedReleaseContainerSpec
{
    public required string TargetId { get; init; }

    public string? CurrentImage { get; init; }

    public required string DesiredImage { get; init; }

    public bool RequiresExplicitApproval { get; init; } = true;
}

public sealed record CoordinatedReleaseContext
{
    public required string PackageId { get; init; }

    public required string TargetEnvironment { get; init; }

    public required CoordinatedReleaseContainerSpec Container { get; init; }

    public required MetadataReleaseExecutionPlan MetadataPlan { get; init; }

    public CoordinatedReleaseStep CurrentStep { get; init; } = CoordinatedReleaseStep.Preflight;

    public IReadOnlyList<CoordinatedReleaseStepState> Steps { get; init; } = Array.Empty<CoordinatedReleaseStepState>();

    public string? ContainerOperationId { get; init; }

    public string? MetadataOperationId { get; init; }

    public bool ContainerGateApproved { get; init; }

    public bool DataGateApproved { get; init; }

    public CoordinatedReleaseHumanApproval? ContainerHumanApproval { get; init; }

    public CoordinatedReleaseHumanApproval? DataHumanApproval { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Blockers { get; init; } = Array.Empty<string>();

    public string? RequesterSubject { get; init; }

    public string? RequesterName { get; init; }

    public string? RequesterAuthenticationType { get; init; }

    public IReadOnlyList<string> RequesterRoles { get; init; } = Array.Empty<string>();

    public string? RequesterTenantId { get; init; }

    public string? RequesterTenantClaimType { get; init; }

    public string? GateApprovedBy { get; init; }

    public string? RollbackRequestedBy { get; init; }
}

public sealed record CoordinatedReleaseStepState
{
    public required CoordinatedReleaseStep Step { get; init; }

    public required CoordinatedReleaseStepStatus Status { get; init; }

    public bool RequiresApproval { get; init; }

    public bool IsReversible { get; init; }

    public string? Detail { get; init; }

    public DateTimeOffset At { get; init; }
}
