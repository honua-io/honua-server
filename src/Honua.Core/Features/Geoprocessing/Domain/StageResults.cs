// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

/// <summary>
/// Result of structural and policy validation for an analysis plan.
/// </summary>
public sealed record PlanValidationResult
{
    /// <summary>
    /// Whether the plan is structurally valid and can be executed.
    /// </summary>
    public required bool IsExecutable { get; init; }

    /// <summary>
    /// Whether the plan requires explicit approval before execution.
    /// </summary>
    public bool RequiresApproval { get; init; }

    /// <summary>
    /// Validation failures that prevent execution.
    /// </summary>
    public IReadOnlyList<GeoprocessingValidationFailure> Violations { get; init; } = [];

    /// <summary>
    /// Non-fatal warnings generated during validation.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Result of a dry-run estimation for an analysis plan.
/// </summary>
public sealed record DryRunResult
{
    /// <summary>
    /// Estimated wall-clock duration in seconds. Only meaningful when
    /// <see cref="DurationEstimateAvailable"/> is <see langword="true"/>; when it is
    /// <see langword="false"/> this value is a placeholder (typically <c>0</c>), not
    /// a prediction, because the dry-run path does not yet model runtime cost.
    /// </summary>
    public double EstimatedDurationSeconds { get; init; }

    /// <summary>
    /// Whether <see cref="EstimatedDurationSeconds"/> is a real estimate. Defaults to
    /// <see langword="false"/> so callers never read a placeholder <c>0</c> as a factual
    /// "instant" prediction. A duration heuristic can set this <see langword="true"/>
    /// once it produces a modelled value.
    /// </summary>
    public bool DurationEstimateAvailable { get; init; }

    /// <summary>
    /// Artifact types the plan is expected to produce.
    /// </summary>
    public IReadOnlyList<ArtifactKind> EstimatedArtifacts { get; init; } = [];

    /// <summary>
    /// Observable side effects the plan would produce.
    /// </summary>
    public IReadOnlyList<string> SideEffects { get; init; } = [];
}
