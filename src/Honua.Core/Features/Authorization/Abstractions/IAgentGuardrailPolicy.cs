// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Licensing.Domain;

namespace Honua.Core.Features.Authorization.Abstractions;

/// <summary>
/// Shared policy that maps the active edition onto the AI-operations guardrail
/// ladder for an agent-initiated operation (#1631):
/// <list type="bullet">
///   <item>Community — direct agent operability, no enforced guardrails.</item>
///   <item>Pro — validation layer: plan + dry-run + validate and a pre-change
///   snapshot/backup gate before apply.</item>
///   <item>Enterprise — approval + policy: the validation layer plus
///   human-in-the-loop approval, policy-scoped permissions, and immutable
///   audit.</item>
/// </list>
/// This is a single, protocol-neutral enforcement point. Protocol adapters
/// (MCP mutate tools, the spec apply engine, gRPC ProcessService, admin
/// mutations) must consume it rather than re-deriving edition-aware guardrail
/// behavior per protocol.
/// </summary>
public interface IAgentGuardrailPolicy
{
    /// <summary>
    /// Resolves the guardrail level that applies at a given edition, independent
    /// of any specific operation. Community maps to
    /// <see cref="AgentGuardrailLevel.Direct"/>, Pro to
    /// <see cref="AgentGuardrailLevel.Validated"/>, and Enterprise to
    /// <see cref="AgentGuardrailLevel.Approval"/>.
    /// </summary>
    /// <param name="edition">The active platform edition.</param>
    /// <returns>The guardrail level for the edition.</returns>
    AgentGuardrailLevel LevelFor(HonuaEdition edition);

    /// <summary>
    /// Evaluates the guardrails that apply to an agent-initiated operation using
    /// the current active license snapshot.
    /// </summary>
    /// <param name="operation">The operation being attempted.</param>
    /// <returns>The guardrail decision for the operation.</returns>
    AgentGuardrailDecision Evaluate(AgentOperationDescriptor operation);
}
