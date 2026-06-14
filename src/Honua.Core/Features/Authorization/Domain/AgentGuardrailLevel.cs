// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Authorization.Domain;

/// <summary>
/// Edition-driven guardrail level applied to an agent-initiated operation.
/// Models the AI-operations guardrail ladder (#1631): Community runs agent
/// operations directly with no enforced flow, Pro inserts a validation layer
/// (planned, dry-run, validated, pre-change snapshot before apply), and
/// Enterprise additionally requires human approval and policy-scoped audit.
/// </summary>
/// <remarks>
/// Values are ordered so a numerically higher level is strictly more
/// restrictive. Each level is cumulative: <see cref="Approval"/> implies the
/// <see cref="Validated"/> guardrails as well.
/// </remarks>
public enum AgentGuardrailLevel
{
    /// <summary>
    /// Community posture: full agent operability with no enforced guardrails.
    /// Discipline is bring-your-own (scoped credentials, operator-owned backups).
    /// </summary>
    Direct = 0,

    /// <summary>
    /// Pro posture: every agent-initiated change is planned, dry-run, and
    /// validated, with a pre-change snapshot/backup gate, before apply.
    /// </summary>
    Validated = 1,

    /// <summary>
    /// Enterprise posture: the validation layer plus human-in-the-loop approval,
    /// policy-scoped agent permissions, and immutable audit of agent actions.
    /// </summary>
    Approval = 2,
}
