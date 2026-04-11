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
    /// Estimated wall-clock duration in seconds.
    /// </summary>
    public double EstimatedDurationSeconds { get; init; }

    /// <summary>
    /// Artifact types the plan is expected to produce.
    /// </summary>
    public IReadOnlyList<ArtifactKind> EstimatedArtifacts { get; init; } = [];

    /// <summary>
    /// Observable side effects the plan would produce.
    /// </summary>
    public IReadOnlyList<string> SideEffects { get; init; } = [];
}
