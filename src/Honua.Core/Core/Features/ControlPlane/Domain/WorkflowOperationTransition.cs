// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Domain;

public enum WorkflowOperationTransitionKind
{
    Created,

    Submitted,

    Promoted,

    RolledBack,

    ManualInterventionRequired
}

public sealed record WorkflowOperationTransition
{
    public required WorkflowOperationRecord Operation { get; init; }

    public required WorkflowOperationTransitionKind Kind { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public string OperationId => Operation.OperationId;

    public string? TargetId => Operation.Deploy?.TargetId;

    public string? ReleaseId =>
        Operation.Deploy?.DesiredRevision ?? Operation.MetadataRelease?.PackageId;

    public string? CorrelationId => Operation.Audit.CorrelationId;
}
