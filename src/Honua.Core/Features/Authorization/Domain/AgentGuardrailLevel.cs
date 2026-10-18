// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Authorization.Domain;

/// <summary>
/// Edition-driven guardrail level applied to an agent-initiated operation.
/// Community has no additional edition-driven governance. Pro and Enterprise
/// include validation, pre-change snapshots, approval and policy-scoped audit.
/// Authorization and tenant isolation apply independently in every edition.
/// </summary>
/// <remarks>
/// Values are ordered so a numerically higher level is strictly more
/// restrictive. Each level is cumulative: <see cref="Approval"/> implies the
/// <see cref="Validated"/> guardrails as well.
/// </remarks>
public enum AgentGuardrailLevel
{
    /// <summary>
    /// No additional edition-driven governance. Operation permissions, tenant
    /// isolation and baseline safety checks still apply.
    /// </summary>
    Direct = 0,

    /// <summary>
    /// Validation-only level retained for contract compatibility: changes are planned, dry-run, and
    /// validated, with a pre-change snapshot/backup gate, before apply.
    /// </summary>
    Validated = 1,

    /// <summary>
    /// Pro and Enterprise posture: validation plus human-in-the-loop approval,
    /// policy-scoped agent permissions, and immutable audit of agent actions.
    /// </summary>
    Approval = 2,
}
