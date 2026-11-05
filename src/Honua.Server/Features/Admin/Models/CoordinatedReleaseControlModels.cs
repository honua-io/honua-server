// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

public sealed record CreateCoordinatedReleaseOperationRequest
{
    public string? PackageId { get; init; }

    public string? TargetEnvironment { get; init; }

    public string? ContainerTargetId { get; init; }

    public string? DesiredImage { get; init; }

    public string? CurrentImage { get; init; }

    public string? ResourceSemanticId { get; init; }

    public string? NewFieldName { get; init; }

    public string? NewFieldType { get; init; }

    public string? DataPopulateWorkloadId { get; init; }

    // The release is rejected before any mutation unless every populated field is the new nullable field.
    public IReadOnlyList<string>? DataPopulateFields { get; init; }

    public string? ScriptId { get; init; }

    public string? Reason { get; init; }

    public string? IdempotencyKey { get; init; }

    public string? CorrelationId { get; init; }
}

public sealed record CoordinatedReleaseActionRequest
{
    public string? Reason { get; init; }
}

public sealed record CoordinatedReleaseArtifactResponse
{
    public required string Kind { get; init; }

    public required string Title { get; init; }

    public required string Desired { get; init; }

    public string? Current { get; init; }
}

public sealed record CoordinatedReleaseStepResponse
{
    public required string Step { get; init; }

    public required string Status { get; init; }

    public bool RequiresApproval { get; init; }

    public bool IsReversible { get; init; }

    public string? Detail { get; init; }
}

public sealed record CoordinatedReleaseOperationResponse
{
    public required string OperationId { get; init; }

    public required string PackageId { get; init; }

    public required string Status { get; init; }

    public required string CurrentStep { get; init; }

    public required string TargetEnvironment { get; init; }

    public string? CurrentPhase { get; init; }

    public IReadOnlyList<CoordinatedReleaseArtifactResponse> Artifacts { get; init; } = [];

    public IReadOnlyList<CoordinatedReleaseStepResponse> Steps { get; init; } = [];

    public bool ContainerGateApproved { get; init; }

    public bool DataGateApproved { get; init; }

    public string? ContainerOperationId { get; init; }

    public string? MetadataOperationId { get; init; }

    public bool RollbackReady { get; init; }

    public IReadOnlyList<string> Blockers { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public string? ErrorMessage { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }
}
