// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Grounding.Domain;

/// <summary>
/// Canonical workflow families a natural-language operator request can map to.
/// Mirrors the taxonomy in AI_OPERATOR_CONTRACT.md and is the classifier
/// output consumed by downstream planning (honua-server-728) and orchestration
/// (honua-devops-29).
/// </summary>
public enum WorkflowFamily
{
    /// <summary>
    /// Analytical workflow producing feature layers, tables, or scalars.
    /// </summary>
    Analyze,

    /// <summary>
    /// Publishing workflow that promotes a source artifact to a managed target.
    /// </summary>
    PublishData,

    /// <summary>
    /// Application or dashboard composition workflow.
    /// </summary>
    BuildApp,

    /// <summary>
    /// Deployment automation workflow.
    /// </summary>
    AutomateDeploy
}

/// <summary>
/// Confidence band attached to a classification or candidate score.
/// </summary>
public enum ConfidenceBand
{
    /// <summary>
    /// Score below <c>GroundingOptions.MediumConfidenceFloor</c>.
    /// </summary>
    Low,

    /// <summary>
    /// Score at or above <c>GroundingOptions.MediumConfidenceFloor</c> and
    /// below <c>GroundingOptions.HighConfidenceFloor</c>.
    /// </summary>
    Medium,

    /// <summary>
    /// Score at or above <c>GroundingOptions.HighConfidenceFloor</c>.
    /// </summary>
    High
}

/// <summary>
/// Discrete category of candidate surfaced by the grounding service.
/// </summary>
public enum CandidateKind
{
    /// <summary>
    /// A feature layer, raster dataset, or similar dataset resource.
    /// </summary>
    Dataset,

    /// <summary>
    /// A geoprocessing operation from the process catalog.
    /// </summary>
    Process,

    /// <summary>
    /// A reusable workflow template.
    /// </summary>
    Template,

    /// <summary>
    /// A style or symbology reference.
    /// </summary>
    Style,

    /// <summary>
    /// A deployment target surface.
    /// </summary>
    Deployment
}

/// <summary>
/// Error categories emitted by the grounding service.
/// </summary>
public enum GroundingErrorKind
{
    /// <summary>
    /// The goal text is empty or whitespace-only.
    /// </summary>
    EmptyGoal,

    /// <summary>
    /// The request specified a workflow family hint that the service does not
    /// implement an end-to-end draft intent for.
    /// </summary>
    UnsupportedWorkflowFamily,

    /// <summary>
    /// The process or layer catalog is unavailable.
    /// </summary>
    CatalogUnavailable,

    /// <summary>
    /// Clarification response targets an intent the service has no draft for.
    /// </summary>
    UnknownIntent
}
