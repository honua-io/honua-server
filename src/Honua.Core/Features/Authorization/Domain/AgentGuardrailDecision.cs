// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Domain;

namespace Honua.Core.Features.Authorization.Domain;

/// <summary>
/// Outcome of evaluating the AI-operations guardrail ladder for an
/// agent-initiated operation (#1631). The decision is purely advisory about
/// which guardrails the caller must satisfy; authorization (whether the caller
/// may perform the operation at all) is evaluated separately by
/// <see cref="Abstractions.IOperatorAuthorizationEvaluator"/>.
/// </summary>
public sealed record AgentGuardrailDecision
{
    /// <summary>
    /// The edition-driven guardrail level enforced for the operation.
    /// </summary>
    public required AgentGuardrailLevel Level { get; init; }

    /// <summary>
    /// The active edition that produced the decision.
    /// </summary>
    public required HonuaEdition Edition { get; init; }

    /// <summary>
    /// Whether the operation must be planned, dry-run, and validated before
    /// apply. True at <see cref="AgentGuardrailLevel.Validated"/> and above for
    /// mutating operations.
    /// </summary>
    public bool RequiresValidation { get; init; }

    /// <summary>
    /// Whether a pre-change snapshot/backup must be captured before apply. True
    /// for mutating operations at <see cref="AgentGuardrailLevel.Validated"/> and
    /// above. See <see cref="SnapshotUnsupported"/> when the operation cannot
    /// honor the gate.
    /// </summary>
    public bool RequiresSnapshot { get; init; }

    /// <summary>
    /// Whether human-in-the-loop approval is required before apply. True for
    /// mutating operations at <see cref="AgentGuardrailLevel.Approval"/>.
    /// </summary>
    public bool RequiresApproval { get; init; }

    /// <summary>
    /// Whether policy-scoped permissions and immutable audit apply to the
    /// operation. True at <see cref="AgentGuardrailLevel.Approval"/>.
    /// </summary>
    public bool RequiresPolicyAudit { get; init; }

    /// <summary>
    /// True when the snapshot gate is required but the operation reported it
    /// does not support snapshots, so the gate cannot be satisfied automatically.
    /// </summary>
    public bool SnapshotUnsupported { get; init; }

    /// <summary>
    /// Machine-readable reason codes describing the guardrails that apply.
    /// </summary>
    public IReadOnlyList<string> ReasonCodes { get; init; } = [];

    /// <summary>
    /// Convenience flag: whether any guardrail at all applies. Always false for
    /// the Community/Direct posture and for non-mutating operations.
    /// </summary>
    public bool HasGuardrails =>
        RequiresValidation || RequiresSnapshot || RequiresApproval || RequiresPolicyAudit;
}
