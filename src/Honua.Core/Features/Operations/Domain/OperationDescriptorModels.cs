// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.WorkflowPackages.Domain;

namespace Honua.Core.Features.Operations.Domain;

/// <summary>
/// Policy metadata carried by every operation descriptor. This is the data the policy
/// decision point reasons over and the audit story is built on. The seam exists now;
/// the enforcement engine lands later (Phase 4) so this is additive, not a retrofit.
/// </summary>
public sealed record OperationPolicyMetadata
{
    /// <summary>
    /// How much state an invocation of this operation can affect.
    /// </summary>
    public required OperationBlastRadiusClass BlastRadiusClass { get; init; }

    /// <summary>
    /// The kind of side effect this operation produces.
    /// </summary>
    public required OperationSideEffectClass SideEffectClass { get; init; }

    /// <summary>
    /// Whether the operation is deterministic or AI-assisted.
    /// </summary>
    public required OperationDeterminism Determinism { get; init; }

    /// <summary>
    /// Whether the operation supports a read-only dry run / preview before committing.
    /// </summary>
    public required bool SupportsDryRun { get; init; }
}

/// <summary>
/// Schema descriptor (groundable) for a single composable operation in the Honua
/// Operations Toolset catalog. The descriptor is the half the AI grounds on; it is a
/// distinct type from <see cref="Abstractions.IOperationExecutor"/>, which does the work.
/// </summary>
/// <remarks>
/// The schema half is projectable from <see cref="WorkflowNodeDefinition"/>, but the
/// descriptor is its own type — it is not a <see cref="WorkflowNodeDefinition"/>. A node
/// definition is pure schema with no execution/approval/side-effect semantics; the
/// descriptor adds the execution kind, approval model, and policy metadata that the
/// toolset and its guardrail seam need.
/// </remarks>
public sealed record OperationDescriptor : IOperationDescriptor
{
    /// <inheritdoc />
    public required string OperationId { get; init; }

    /// <inheritdoc />
    public required string ProviderId { get; init; }

    /// <inheritdoc />
    public required string Title { get; init; }

    /// <inheritdoc />
    public required string Description { get; init; }

    /// <inheritdoc />
    public required string Category { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<OperationParameterDescriptor> InputSchema { get; init; } = [];

    /// <inheritdoc />
    public IReadOnlyList<OperationParameterDescriptor> OutputSchema { get; init; } = [];

    /// <inheritdoc />
    public required OperationExecutionKind ExecutionKind { get; init; }

    /// <inheritdoc />
    public required OperationApprovalModel ApprovalModel { get; init; }

    /// <inheritdoc />
    public required OperationPolicyMetadata Policy { get; init; }

    /// <inheritdoc />
    public OperationGroundingMetadata? Grounding { get; init; }
}

/// <summary>
/// A single input or output parameter on an operation descriptor. Reuses the AOT-safe
/// constrained <see cref="WorkflowSchemaDefinition"/> so the toolset catalog projects
/// cleanly from the workflow node registry and serializes under source-generated JSON.
/// </summary>
public sealed record OperationParameterDescriptor
{
    /// <summary>
    /// Machine-readable parameter name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Human-readable label.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Whether the parameter must be supplied.
    /// </summary>
    public bool Required { get; init; }

    /// <summary>
    /// Constrained value schema for the parameter.
    /// </summary>
    public required WorkflowSchemaDefinition Schema { get; init; }
}

/// <summary>
/// Placeholder for grounding metadata (lexical/semantic retrieval hints, synonyms,
/// few-shot exemplar refs). Unused in this de-risking spike — the AI grounding layer is
/// a later phase — but reserved on the descriptor so it is not a retrofit.
/// </summary>
public sealed record OperationGroundingMetadata
{
    /// <summary>
    /// Alternate phrasings/synonyms that should resolve to this operation. Empty for now.
    /// </summary>
    public IReadOnlyList<string> Synonyms { get; init; } = [];
}

/// <summary>
/// Versioned snapshot of the operation grounding catalog: every descriptor contributed by
/// every registered descriptor provider. Mirrors <c>WorkflowNodeRegistrySnapshot</c>.
/// </summary>
public sealed record OperationCatalogSnapshot
{
    /// <summary>
    /// Stable version fingerprint for this catalog snapshot.
    /// </summary>
    public required string CatalogVersion { get; init; }

    /// <summary>
    /// UTC timestamp when the snapshot was generated.
    /// </summary>
    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>
    /// Provider identifiers that contributed descriptors, in sorted order.
    /// </summary>
    public IReadOnlyList<string> ProviderIds { get; init; } = [];

    /// <summary>
    /// All operation descriptors available in the catalog.
    /// </summary>
    public required IReadOnlyList<OperationDescriptor> Operations { get; init; }
}
