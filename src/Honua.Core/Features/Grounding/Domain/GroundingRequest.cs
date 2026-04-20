// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Grounding.Domain;

/// <summary>
/// Inputs to a single grounding pass. Carries the freeform goal, caller
/// context, optional constraint overrides, and prior clarification answers
/// when the request is a follow-up turn.
/// </summary>
public sealed record GroundingRequest
{
    /// <summary>
    /// Freeform natural-language description of the operator's goal.
    /// </summary>
    public required string Goal { get; init; }

    /// <summary>
    /// Optional explicit workflow-family hint. When present, the classifier
    /// honors the hint instead of deriving a family from goal tokens.
    /// </summary>
    public WorkflowFamily? WorkflowFamilyHint { get; init; }

    /// <summary>
    /// Optional intent constraints the caller has already pinned (AOI, SRID,
    /// time window, units).
    /// </summary>
    public IntentConstraints? Constraints { get; init; }

    /// <summary>
    /// Explicit dataset or layer references the caller has already supplied.
    /// </summary>
    public IReadOnlyList<string> ExplicitInputs { get; init; } = [];

    /// <summary>
    /// Policy governing how the drafter handles inferred assumptions.
    /// </summary>
    public AssumptionPolicy AssumptionPolicy { get; init; } = AssumptionPolicy.AskWhenMaterial;

    /// <summary>
    /// Caller context, used to filter authorized candidates and pin workspace
    /// scope.
    /// </summary>
    public CallerContext Context { get; init; } = new();

    /// <summary>
    /// Answers supplied by the caller in response to a prior
    /// <see cref="Geoprocessing.Domain.ClarificationRequest"/>. Null for the
    /// initial turn.
    /// </summary>
    public ClarificationResponse? ClarificationResponse { get; init; }

    /// <summary>
    /// Pre-existing intent identifier when the request is a clarification
    /// turn. Null on the initial pass; the service allocates one.
    /// </summary>
    public string? IntentId { get; init; }
}

/// <summary>
/// Environmental context about the caller. Keeps the grounding service
/// stateless — the caller is responsible for carrying workspace and prior
/// intent references across turns.
/// </summary>
public sealed record CallerContext
{
    /// <summary>
    /// Workspace the request originates from, when applicable.
    /// </summary>
    public string? WorkspaceId { get; init; }

    /// <summary>
    /// Identifier of the intent that produced the most recent result the
    /// caller is following up on, when applicable.
    /// </summary>
    public string? PriorIntentId { get; init; }

    /// <summary>
    /// Promotion scope ("personal", "shared", "workspace", etc.) used to
    /// authorize deployment or publish suggestions.
    /// </summary>
    public string? PromotionScope { get; init; }
}
