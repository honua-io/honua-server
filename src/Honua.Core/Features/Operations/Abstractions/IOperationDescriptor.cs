// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Abstractions;

/// <summary>
/// Groundable schema descriptor for a single composable operation in the Honua Operations
/// Toolset. This is the half the AI grounds on. It is a distinct type from
/// <see cref="IOperationExecutor"/> (which does the work): the descriptor carries only
/// schema, execution kind, approval model, and policy metadata — never execution behavior.
/// </summary>
/// <remarks>
/// The schema half is projectable from <c>WorkflowNodeDefinition</c>, but a descriptor is
/// NOT a <c>WorkflowNodeDefinition</c>: a node definition is pure schema, while a descriptor
/// adds the execution/approval/policy semantics the toolset and its guardrail seam require.
/// </remarks>
public interface IOperationDescriptor
{
    /// <summary>
    /// Stable operation identifier, for example <c>service.publish</c>.
    /// </summary>
    string OperationId { get; }

    /// <summary>
    /// Identifier of the provider that owns this operation.
    /// </summary>
    string ProviderId { get; }

    /// <summary>
    /// Human-readable operation title.
    /// </summary>
    string Title { get; }

    /// <summary>
    /// Short operation description.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Palette category for grouping related operations.
    /// </summary>
    string Category { get; }

    /// <summary>
    /// Input parameter schema.
    /// </summary>
    IReadOnlyList<OperationParameterDescriptor> InputSchema { get; }

    /// <summary>
    /// Output schema describing what the operation produces.
    /// </summary>
    IReadOnlyList<OperationParameterDescriptor> OutputSchema { get; }

    /// <summary>
    /// How the operation is executed (synchronous / job / proposal handoff).
    /// </summary>
    OperationExecutionKind ExecutionKind { get; }

    /// <summary>
    /// Human-gate model the operation flows through.
    /// </summary>
    OperationApprovalModel ApprovalModel { get; }

    /// <summary>
    /// Policy metadata the policy decision point reasons over.
    /// </summary>
    OperationPolicyMetadata Policy { get; }

    /// <summary>
    /// Reserved grounding metadata; unused in the current spike.
    /// </summary>
    OperationGroundingMetadata? Grounding { get; }
}
