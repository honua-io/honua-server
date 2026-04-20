// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Publishing.Domain;

namespace Honua.Core.Features.Grounding.Domain;

/// <summary>
/// Canonical draft-intent envelope emitted by the grounding service. Carries
/// one concrete typed intent record (analysis or publishing) or just the
/// envelope stub fields for workflow families whose domain records are still
/// in flight (BuildApp, AutomateDeploy).
/// </summary>
public sealed record DraftIntent
{
    /// <summary>
    /// Stable intent identifier. Used as the join key across turns.
    /// </summary>
    public required string IntentId { get; init; }

    /// <summary>
    /// Goal text as received from the caller.
    /// </summary>
    public required string Goal { get; init; }

    /// <summary>
    /// Workflow family this draft belongs to.
    /// </summary>
    public required WorkflowFamily WorkflowFamily { get; init; }

    /// <summary>
    /// Output artifact kinds the draft requests.
    /// </summary>
    public IReadOnlyList<ArtifactKind> RequestedOutputs { get; init; } = [];

    /// <summary>
    /// Policy for how the drafter handles inferred assumptions.
    /// </summary>
    public AssumptionPolicy AssumptionPolicy { get; init; } = AssumptionPolicy.AskWhenMaterial;

    /// <summary>
    /// Canonical <see cref="AnalysisIntent"/> when
    /// <see cref="WorkflowFamily"/> is <see cref="WorkflowFamily.Analyze"/>.
    /// Null otherwise.
    /// </summary>
    public AnalysisIntent? Analysis { get; init; }

    /// <summary>
    /// Canonical <see cref="PublishIntent"/> when
    /// <see cref="WorkflowFamily"/> is <see cref="WorkflowFamily.PublishData"/>
    /// and the drafter could select a source kind. Null otherwise.
    /// </summary>
    public PublishIntent? Publishing { get; init; }

    /// <summary>
    /// Provenance record summarising sources, processes, assumptions, and
    /// clarifications associated with this draft.
    /// </summary>
    public required ProvenanceRecord Provenance { get; init; }
}
