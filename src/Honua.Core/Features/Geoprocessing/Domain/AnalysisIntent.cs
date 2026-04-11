// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

/// <summary>
/// Entry point for every geoprocessing workflow. Represents a partially structured user goal
/// that the system will ground, clarify, and compile into an executable analysis plan.
/// </summary>
public sealed record AnalysisIntent
{
    /// <summary>
    /// Unique identifier for this intent.
    /// </summary>
    public required string IntentId { get; init; }

    /// <summary>
    /// Natural-language description of the user's goal.
    /// </summary>
    public required string Goal { get; init; }

    /// <summary>
    /// Workflow mode hint (e.g., "analysis", "publish_data", "build_app").
    /// </summary>
    public string? Mode { get; init; }

    /// <summary>
    /// Output artifact types the user has explicitly requested.
    /// </summary>
    public IReadOnlyList<ArtifactKind> RequestedOutputs { get; init; } = [];

    /// <summary>
    /// Optional spatial, temporal, and unit constraints on the analysis.
    /// </summary>
    public IntentConstraints? Constraints { get; init; }

    /// <summary>
    /// Explicit dataset or layer references provided by the user.
    /// </summary>
    public IReadOnlyList<string> Inputs { get; init; } = [];

    /// <summary>
    /// Strategy for handling assumptions during workflow compilation.
    /// </summary>
    public AssumptionPolicy AssumptionPolicy { get; init; } = AssumptionPolicy.AskWhenMaterial;
}

/// <summary>
/// Spatial, temporal, and unit constraints applied to an analysis intent.
/// </summary>
public sealed record IntentConstraints
{
    /// <summary>
    /// Area of interest as WKT, bounding box string, or named area.
    /// </summary>
    public string? AreaOfInterest { get; init; }

    /// <summary>
    /// EPSG SRID for the area of interest. When <c>null</c>, WGS 84 (EPSG:4326) is assumed.
    /// </summary>
    public int? SpatialReferenceId { get; init; }

    /// <summary>
    /// Start of the time window for temporal filtering.
    /// </summary>
    public DateTimeOffset? TimeWindowStart { get; init; }

    /// <summary>
    /// End of the time window for temporal filtering.
    /// </summary>
    public DateTimeOffset? TimeWindowEnd { get; init; }

    /// <summary>
    /// Preferred unit system (e.g., "meters", "feet").
    /// </summary>
    public string? Units { get; init; }
}
