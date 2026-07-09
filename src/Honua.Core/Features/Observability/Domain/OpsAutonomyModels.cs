// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;

namespace Honua.Core.Features.Observability.Domain;

/// <summary>
/// Per-rule autonomy mode for deterministic ops-finding remediation.
/// </summary>
public enum OpsAutonomyMode
{
    /// <summary>Findings only surface a recommended action for operator proposal/approval.</summary>
    ProposeOnly = 0,

    /// <summary>Findings may be auto-applied through the operation gateway when all guardrails pass.</summary>
    AutoApply = 1,
}

/// <summary>
/// Terminal outcome recorded for an autonomous remediation attempt.
/// </summary>
public enum OpsAutonomyActionOutcome
{
    /// <summary>The action completed successfully.</summary>
    Succeeded = 0,

    /// <summary>The action failed or could not be executed.</summary>
    Failed = 1,

    /// <summary>The action was rolled back or reverted after a regression.</summary>
    RolledBack = 2,
}

/// <summary>
/// Durable autonomy policy for a single deterministic ops-finding rule.
/// </summary>
public sealed record OpsAutonomyPolicy
{
    /// <summary>Gets the kebab-case ops-finding rule identifier.</summary>
    public required string Rule { get; init; }

    /// <summary>Gets the autonomy mode for the rule.</summary>
    public OpsAutonomyMode Mode { get; init; } = OpsAutonomyMode.ProposeOnly;

    /// <summary>Gets the maximum autonomous actions permitted in the rolling window.</summary>
    public int MaxAutoActionsPerWindow { get; init; } = 1;

    /// <summary>Gets the rolling rate-limit window.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Gets the maximum allowed blast-radius value for a single action.</summary>
    public int MaxBlastRadius { get; init; } = 1;

    /// <summary>Gets the UTC time the durable policy was last updated, when known.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>Gets the actor that last updated the durable policy, when known.</summary>
    public string? UpdatedBy { get; init; }
}

/// <summary>
/// Global autonomy settings that apply before any per-rule policy.
/// </summary>
public sealed record OpsAutonomySettings
{
    /// <summary>Gets a value indicating whether all autonomous remediation is disabled.</summary>
    public bool KillSwitchEnabled { get; init; }

    /// <summary>Gets the UTC time the settings were last updated, when known.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>Gets the actor that last updated the settings, when known.</summary>
    public string? UpdatedBy { get; init; }
}

/// <summary>
/// Aggregate per-rule outcome counters used by the console to justify policy graduation.
/// </summary>
public sealed record OpsAutonomyTrackRecord
{
    /// <summary>Gets the rule identifier.</summary>
    public required string Rule { get; init; }

    /// <summary>Gets the number of proposals raised for the rule.</summary>
    public long ProposalsRaised { get; init; }

    /// <summary>Gets the number of proposals approved for the rule.</summary>
    public long ProposalsApproved { get; init; }

    /// <summary>Gets the number of proposals rejected for the rule.</summary>
    public long ProposalsRejected { get; init; }

    /// <summary>Gets the number of autonomous actions that succeeded for the rule.</summary>
    public long AutoApplied { get; init; }

    /// <summary>Gets the number of autonomous actions rolled back or reverted for the rule.</summary>
    public long RolledBack { get; init; }

    /// <summary>Gets the number of autonomous actions that failed for the rule.</summary>
    public long Failed { get; init; }

    /// <summary>Gets the first known activity timestamp for the rule.</summary>
    public DateTimeOffset? FirstActivityAt { get; init; }

    /// <summary>Gets the most recent known activity timestamp for the rule.</summary>
    public DateTimeOffset? LastActivityAt { get; init; }
}

/// <summary>
/// Policy plus the aggregate track record for a rule.
/// </summary>
public sealed record OpsAutonomyPolicySnapshot
{
    /// <summary>Gets the effective or durable policy.</summary>
    public required OpsAutonomyPolicy Policy { get; init; }

    /// <summary>Gets the aggregate outcome track record for the rule.</summary>
    public required OpsAutonomyTrackRecord TrackRecord { get; init; }
}

/// <summary>
/// Request to reserve one autonomous remediation action under a rule's rate cap.
/// </summary>
public sealed record OpsAutonomyReservationRequest
{
    /// <summary>Gets the rule identifier.</summary>
    public required string Rule { get; init; }

    /// <summary>Gets the deterministic finding identifier used as the idempotency key.</summary>
    public required string FindingId { get; init; }

    /// <summary>Gets the operation class routed through the gateway.</summary>
    public required OperationClass OperationClass { get; init; }

    /// <summary>Gets the optional action discriminator for admin-config operations.</summary>
    public string? ActionDiscriminator { get; init; }

    /// <summary>Gets the action blast-radius value used by the policy guard.</summary>
    public int BlastRadius { get; init; } = 1;

    /// <summary>Gets the maximum autonomous actions permitted in the window.</summary>
    public int MaxAutoActionsPerWindow { get; init; } = 1;

    /// <summary>Gets the rolling rate-limit window.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromHours(1);
}

/// <summary>
/// Result of attempting to reserve one autonomous remediation action.
/// </summary>
public sealed record OpsAutonomyReservationResult
{
    /// <summary>Gets a value indicating whether a reservation was created.</summary>
    public required bool Reserved { get; init; }

    /// <summary>Gets the durable reservation identifier when <see cref="Reserved"/> is true.</summary>
    public string? ReservationId { get; init; }

    /// <summary>Gets the reason reservation was denied or reused, when available.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Result of evaluating a finding for background auto-apply eligibility.
/// </summary>
public sealed record OpsAutonomyFindingDecision
{
    /// <summary>Gets a value indicating whether the finding can be handed to the gateway for auto-apply.</summary>
    public required bool CanAutoApply { get; init; }

    /// <summary>Gets the effective policy used for the decision, when a rule was resolved.</summary>
    public OpsAutonomyPolicy? Policy { get; init; }

    /// <summary>Gets the guardrail reason when the finding is not eligible.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Result of a route-time autonomy evaluation in the operation gateway.
/// </summary>
public sealed record OpsAutonomyRouteDecision
{
    /// <summary>Gets a value indicating whether the gateway should auto-apply the request.</summary>
    public required bool ShouldAutoApply { get; init; }

    /// <summary>Gets the guardrail decision to use for direct execution when auto-apply is allowed.</summary>
    public GuardrailDecision? Decision { get; init; }

    /// <summary>Gets the durable action reservation identifier when auto-apply is allowed.</summary>
    public string? ReservationId { get; init; }

    /// <summary>Gets the effective policy used for the decision, when a rule was resolved.</summary>
    public OpsAutonomyPolicy? Policy { get; init; }

    /// <summary>Gets the route-time guardrail reason.</summary>
    public string? Reason { get; init; }
}
