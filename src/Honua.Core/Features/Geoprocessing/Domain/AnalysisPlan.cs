// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Raster;

namespace Honua.Core.Features.Geoprocessing.Domain;

/// <summary>
/// An executable analysis plan compiled from a grounded intent. Represents a directed acyclic graph
/// of processing steps that the execution engine will run.
/// </summary>
public sealed record AnalysisPlan
{
    /// <summary>
    /// Unique identifier for this plan.
    /// </summary>
    public required string PlanId { get; init; }

    /// <summary>
    /// Back-reference to the originating intent.
    /// </summary>
    public required string IntentId { get; init; }

    /// <summary>
    /// Ordered list of processing steps forming the execution DAG.
    /// </summary>
    public required IReadOnlyList<AnalysisPlanStep> Steps { get; init; }

    /// <summary>
    /// Output artifact types this plan promises to produce.
    /// </summary>
    public IReadOnlyList<ArtifactKind> Outputs { get; init; } = [];

    /// <summary>
    /// Non-fatal warnings generated during plan compilation.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// Raster publication store and target authorized for this exact plan. This is populated by
    /// the workflow authorization gate and persisted with the workflow/run plan; ordinary direct
    /// submissions may omit it and resolve the operator configuration while their live principal
    /// is still available.
    /// </summary>
    public RasterOutputPublicationPin? RasterOutputPublication { get; init; }
}

/// <summary>
/// A single step within an analysis plan, representing one unit of work in the execution DAG.
/// </summary>
public sealed record AnalysisPlanStep
{
    /// <summary>
    /// Unique identifier for this step within the plan.
    /// </summary>
    public required string StepId { get; init; }

    /// <summary>
    /// Category of work this step performs.
    /// </summary>
    public required AnalysisPlanStepKind Kind { get; init; }

    /// <summary>
    /// Identifier of the geoprocessing operation to execute, for geoprocess steps.
    /// </summary>
    public string? ProcessId { get; init; }

    /// <summary>
    /// Opaque step-specific parameters matching the ControlPlane Parameters pattern.
    /// </summary>
    public IReadOnlyDictionary<string, string> Inputs { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Typed raster inputs keyed by the process parameter they bind (for example
    /// <c>source</c>). Reference descriptors are projected onto the durable worker
    /// contract without materializing raster bytes in the serving process.
    /// </summary>
    public IReadOnlyDictionary<string, RasterSourceDescriptor> RasterSources { get; init; } =
        new Dictionary<string, RasterSourceDescriptor>();

    /// <summary>
    /// Step identifiers that must complete before this step can execute.
    /// </summary>
    public IReadOnlyList<string> DependsOn { get; init; } = [];
}
