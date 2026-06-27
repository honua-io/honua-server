// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.WorkflowPackages.Domain;

namespace Honua.Core.Features.Operations.Domain;

/// <summary>
/// Server-owned description of a single composable operation in the Honua Operations
/// Toolset. An operation is one level up from a workflow node: it bundles a parameter
/// contract, an execution lane, an approval lane, and the policy metadata a guardrail
/// decision point evaluates.
/// </summary>
/// <remarks>
/// This mirrors <see cref="WorkflowNodeDefinition"/> but adds the execution/approval
/// lane and policy metadata that make every operation a natural policy-enforcement
/// point. The deterministic console UI and the AI orchestrator invoke the same
/// operation; the lane and policy metadata are how guardrails attach to it.
/// </remarks>
public sealed record HonuaOperationDefinition
{
    /// <summary>
    /// Stable operation identifier, for example <c>service.publish</c>.
    /// </summary>
    public required string OperationId { get; init; }

    /// <summary>
    /// Provider that owns the operation.
    /// </summary>
    public required string ProviderId { get; init; }

    /// <summary>
    /// Human-readable operation title.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Short operation description.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Catalog category for grouping related operations.
    /// </summary>
    public required string Category { get; init; }

    /// <summary>
    /// Parameter schemas used by Console forms, AI function-calling, and server
    /// validation. Reuses the AOT-safe workflow parameter schema type.
    /// </summary>
    public required IReadOnlyList<WorkflowNodeParameterSchema> ParameterSchemas { get; init; }

    /// <summary>
    /// Schema describing the operation's success output payload.
    /// </summary>
    public required WorkflowSchemaDefinition OutputSchema { get; init; }

    /// <summary>
    /// Execution surface this operation routes through.
    /// </summary>
    public required OperationExecutionLane ExecutionLane { get; init; }

    /// <summary>
    /// Human-approval lane this operation lands in.
    /// </summary>
    public required OperationApprovalLane ApprovalLane { get; init; }

    /// <summary>
    /// Policy metadata evaluated by the policy decision point (the guardrail seam).
    /// </summary>
    public required OperationPolicyMetadata Policy { get; init; }

    /// <summary>
    /// Placeholder for grounding metadata used by the AI orchestrator. Not populated
    /// or consumed in this spike; reserved so it never becomes a retrofit.
    /// </summary>
    public OperationGroundingMetadata? Grounding { get; init; }
}

/// <summary>
/// Policy metadata carried by every operation so that an invocation can flow through
/// a tier- and role-aware policy decision point. This is the contract half of the
/// guardrail seam; the enforcement engine is a later phase.
/// </summary>
public sealed record OperationPolicyMetadata
{
    /// <summary>
    /// Coarse blast-radius classification used by the decision point.
    /// </summary>
    public required OperationBlastRadiusClass BlastRadiusClass { get; init; }

    /// <summary>
    /// Side-effect classification used by the decision point.
    /// </summary>
    public required OperationSideEffectClass SideEffectClass { get; init; }

    /// <summary>
    /// Whether the operation is deterministic or AI-assisted.
    /// </summary>
    public required OperationDeterminism Determinism { get; init; }

    /// <summary>
    /// Whether the operation supports a bounded dry run / preview before a real run.
    /// </summary>
    public required bool SupportsDryRun { get; init; }
}

/// <summary>
/// Placeholder grounding metadata for AI orchestration. Reserved scaffolding; not
/// used in the de-risking spike.
/// </summary>
public sealed record OperationGroundingMetadata
{
    /// <summary>
    /// Optional grounding hints keyed by hint name. Empty in this spike.
    /// </summary>
    public IReadOnlyDictionary<string, string> Hints { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
