// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;

namespace Honua.Core.Features.Observability.Domain;

public enum OpsAutonomyMode
{
    ProposeOnly = 0,

    AutoApply = 1,
}

public enum OpsAutonomyActionOutcome
{
    Succeeded = 0,

    Failed = 1,

    RolledBack = 2,

    // When verification or compensation cannot prove a safe terminal result, manual intervention is required.
    Indeterminate = 3,

    Canceled = 4,
}

public enum OpsAutonomyProposalResolution
{
    Approved = 0,

    Rejected = 1,
}

public sealed record OpsAutonomyPolicy
{
    public required string Rule { get; init; }

    public OpsAutonomyMode Mode { get; init; } = OpsAutonomyMode.ProposeOnly;

    public int MaxAutoActionsPerWindow { get; init; } = 1;

    public TimeSpan Window { get; init; } = TimeSpan.FromHours(1);

    public int MaxBlastRadius { get; init; } = 1;

    public DateTimeOffset? UpdatedAt { get; init; }

    public string? UpdatedBy { get; init; }
}

public sealed record OpsAutonomySettings
{
    public bool KillSwitchEnabled { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    public string? UpdatedBy { get; init; }
}

public sealed record OpsAutonomyTrackRecord
{
    public required string Rule { get; init; }

    public long ProposalsRaised { get; init; }

    public long ProposalsApproved { get; init; }

    public long ProposalsRejected { get; init; }

    public long AutoApplied { get; init; }

    public long RolledBack { get; init; }

    public long Failed { get; init; }

    public DateTimeOffset? FirstActivityAt { get; init; }

    public DateTimeOffset? LastActivityAt { get; init; }
}

public sealed record OpsAutonomyPolicySnapshot
{
    public required OpsAutonomyPolicy Policy { get; init; }

    public bool IsPersisted { get; init; }

    public required OpsAutonomyTrackRecord TrackRecord { get; init; }
}

public sealed record OpsAutonomyReservationRequest
{
    public required string Rule { get; init; }

    public required string FindingId { get; init; }

    public required OperationClass OperationClass { get; init; }

    public string? ActionDiscriminator { get; init; }

    public int BlastRadius { get; init; } = 1;

    public int MaxAutoActionsPerWindow { get; init; } = 1;

    public TimeSpan Window { get; init; } = TimeSpan.FromHours(1);
}

public sealed record OpsAutonomyReservationResult
{
    public required bool Reserved { get; init; }

    public string? ReservationId { get; init; }

    public string? Reason { get; init; }
}

public sealed record OpsAutonomyFindingDecision
{
    public required bool CanAutoApply { get; init; }

    public OpsAutonomyPolicy? Policy { get; init; }

    public string? Reason { get; init; }
}

public sealed record OpsAutonomyRouteDecision
{
    public required bool ShouldAutoApply { get; init; }

    public GuardrailDecision? Decision { get; init; }

    public string? ReservationId { get; init; }

    public OpsAutonomyPolicy? Policy { get; init; }

    public string? Reason { get; init; }
}
