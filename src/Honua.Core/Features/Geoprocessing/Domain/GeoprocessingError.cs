// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

/// <summary>
/// Structured error produced during a geoprocessing workflow, optionally tied to a specific plan step.
/// </summary>
public sealed record GeoprocessingError
{
    /// <summary>
    /// Category of the error.
    /// </summary>
    public required GeoprocessingErrorKind Kind { get; init; }

    /// <summary>
    /// Human-readable error message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Identifier of the plan step that failed, when applicable.
    /// </summary>
    public string? StepId { get; init; }

    /// <summary>
    /// Validation failures contributing to this error.
    /// </summary>
    public IReadOnlyList<GeoprocessingValidationFailure>? Violations { get; init; }
}

/// <summary>
/// A single validation failure within a geoprocessing error.
/// </summary>
public sealed record GeoprocessingValidationFailure
{
    /// <summary>
    /// Machine-readable violation code.
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    /// Human-readable violation message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Path to the offending field, when applicable.
    /// </summary>
    public string? FieldPath { get; init; }
}
