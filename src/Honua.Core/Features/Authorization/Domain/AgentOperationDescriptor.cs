// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Authorization.Domain;

/// <summary>
/// Transport-neutral description of an agent-initiated operation evaluated by
/// the shared AI-operations guardrail policy (#1631). Protocol adapters (MCP
/// tools, the spec apply engine, gRPC ProcessService, admin mutation paths)
/// build this descriptor and route it through
/// <see cref="Abstractions.IAgentGuardrailPolicy"/> rather than each
/// re-implementing edition-aware guardrail decisions.
/// </summary>
public sealed record AgentOperationDescriptor
{
    /// <summary>
    /// Stable identifier for the agent operation, used in telemetry and audit
    /// (for example <c>honua_execute_plan</c> or <c>spec.apply</c>).
    /// </summary>
    public required string OperationId { get; init; }

    /// <summary>
    /// Protocol surface the operation arrived on (for example <c>mcp</c>,
    /// <c>spec</c>, or <c>grpc</c>). Used for audit and reason-code context.
    /// </summary>
    public string? Protocol { get; init; }

    /// <summary>
    /// Whether the operation mutates server state. Read/discovery/query
    /// operations are never gated by the guardrail ladder regardless of edition;
    /// only mutating operations cross the ladder.
    /// </summary>
    public bool IsMutating { get; init; }

    /// <summary>
    /// Whether the operation is destructive (irreversible without a restore).
    /// Destructive operations are what the Pro pre-change snapshot gate protects
    /// and what Enterprise approval policy most often targets.
    /// </summary>
    public bool IsDestructive { get; init; }

    /// <summary>
    /// Whether the operation supports a pre-change snapshot/backup so the
    /// Validated guardrail can require one before apply. When false, the policy
    /// records that the snapshot gate is unsatisfiable for this operation.
    /// </summary>
    public bool SupportsSnapshot { get; init; } = true;
}
