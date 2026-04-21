// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Grounding.Domain;

/// <summary>
/// Full output of a grounding pass: the classification, the draft intent,
/// ranked candidates, and an optional clarification envelope. The service
/// emits the clarification envelope as a native
/// <see cref="Geoprocessing.Domain.ClarificationRequest"/> so downstream
/// MCP clients (geospatial-mcp-3) consume it without transformation.
/// </summary>
public sealed record GroundingResult
{
    /// <summary>
    /// Workflow-family classification with confidence.
    /// </summary>
    public required WorkflowFamilyClassification WorkflowFamily { get; init; }

    /// <summary>
    /// Canonical draft-intent envelope.
    /// </summary>
    public required DraftIntent DraftIntent { get; init; }

    /// <summary>
    /// Ranked candidates, filtered by caller authorization.
    /// </summary>
    public required CandidateRanking Candidates { get; init; }

    /// <summary>
    /// Clarification envelope emitted when grounding surfaced a material
    /// ambiguity (see <see cref="MaterialAmbiguityFinding"/>). Null when the
    /// caller can proceed to planning without further input.
    /// </summary>
    public ClarificationRequest? Clarification { get; init; }

    /// <summary>
    /// Name of the engine that produced this result (e.g. "deterministic").
    /// Included in telemetry and conformance fixtures so multi-engine
    /// operation is observable.
    /// </summary>
    public required string Engine { get; init; }
}
