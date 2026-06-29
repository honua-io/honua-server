// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Policy;

/// <summary>
/// Configurable guardrail policy bound from the <c>Operations:Policy</c> configuration section.
/// This is the product surface over the <c>IOperationPolicyDecisionPoint</c> seam: instead of
/// the hard-coded pass-through Community default, an operator can allow / deny / require-approval
/// / require-dry-run per operation id, tier, and role without a code change.
/// </summary>
/// <remarks>
/// When <see cref="Enabled"/> is <see langword="false"/> (the default) the policy is inert and
/// behavior is identical to <c>AllowAllPolicyDecisionPoint</c> — every operation is allowed.
/// Rules are evaluated first-match-wins in declared order; when no rule matches, the
/// <see cref="DefaultDecision"/> applies.
/// </remarks>
public sealed class OperationPolicyOptions
{
    /// <summary>
    /// Configuration section name: <c>Operations:Policy</c>.
    /// </summary>
    public const string SectionName = "Operations:Policy";

    /// <summary>
    /// Whether the configurable policy is enforced. When <see langword="false"/> (default) the
    /// decision point is a pass-through allow, preserving the permissive Community default.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Decision applied when no rule in <see cref="Rules"/> matches the invocation. Defaults to
    /// <see cref="PolicyDecisionKind.Allow"/> so an enabled-but-ruleless policy stays permissive.
    /// </summary>
    public PolicyDecisionKind DefaultDecision { get; set; } = PolicyDecisionKind.Allow;

    /// <summary>
    /// Optional reason attached to a decision produced by <see cref="DefaultDecision"/>.
    /// </summary>
    public string? DefaultReason { get; set; }

    /// <summary>
    /// Optional approval lane attached to a <see cref="DefaultDecision"/> of
    /// <see cref="PolicyDecisionKind.RequireApproval"/>.
    /// </summary>
    public string? DefaultApprovalLane { get; set; }

    /// <summary>
    /// Ordered rule set. The first rule whose match criteria are satisfied wins; remaining rules
    /// are not evaluated. Empty by default.
    /// </summary>
    public IList<OperationPolicyRule> Rules { get; set; } = [];
}

/// <summary>
/// A single first-match-wins guardrail rule within <see cref="OperationPolicyOptions"/>. A rule
/// matches when every populated criterion matches the invocation; unset (null/empty) criteria are
/// wildcards. An <see cref="OperationId"/> of <c>"*"</c> (or empty) matches any operation.
/// </summary>
public sealed class OperationPolicyRule
{
    /// <summary>
    /// Operation id this rule targets, or <c>"*"</c> (the default) to match any operation.
    /// Matched case-sensitively (ordinal) against <c>OperationRequest.OperationId</c>.
    /// </summary>
    public string OperationId { get; set; } = "*";

    /// <summary>
    /// Optional tier this rule is scoped to (for example <c>pro</c>, <c>enterprise</c>). When set,
    /// the rule only matches when the caller's tier equals it (case-insensitive). Null matches any tier.
    /// </summary>
    public string? Tier { get; set; }

    /// <summary>
    /// Optional role this rule is scoped to (for example <c>operator</c>, <c>publisher</c>). When set,
    /// the rule only matches when the caller holds it (case-insensitive). Null matches any role.
    /// </summary>
    public string? Role { get; set; }

    /// <summary>
    /// Decision produced when this rule matches.
    /// </summary>
    public PolicyDecisionKind Decision { get; set; } = PolicyDecisionKind.Allow;

    /// <summary>
    /// Optional human-readable reason carried on the produced decision.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// Optional approval lane carried on a <see cref="PolicyDecisionKind.RequireApproval"/> decision.
    /// </summary>
    public string? ApprovalLane { get; set; }
}
