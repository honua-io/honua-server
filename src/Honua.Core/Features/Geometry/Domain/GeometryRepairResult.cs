// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.Geometry.Domain;

/// <summary>
/// Result of geometry repair operations.
/// </summary>
public sealed record GeometryRepairResult
{
    /// <summary>
    /// Whether the repair was successful.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// The repaired geometry WKB bytes.
    /// </summary>
    public byte[]? RepairedWkb { get; init; }

    /// <summary>
    /// List of repairs that were applied.
    /// </summary>
    public ImmutableList<string> RepairsApplied { get; init; } = ImmutableList<string>.Empty;

    /// <summary>
    /// Error message if repair failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// The original validation reason from the topology validator.
    /// </summary>
    public string? OriginalValidationReason { get; init; }

    /// <summary>
    /// Whether the geometry type changed during repair.
    /// </summary>
    public bool GeometryTypeChanged { get; init; }

    /// <summary>
    /// Creates a successful repair result.
    /// </summary>
    public static GeometryRepairResult Repaired(byte[] repairedWkb, IEnumerable<string>? repairs = null) => new()
    {
        Success = true,
        RepairedWkb = repairedWkb,
        RepairsApplied = repairs?.ToImmutableList() ?? ImmutableList<string>.Empty
    };

    /// <summary>
    /// Creates a failed repair result.
    /// </summary>
    public static GeometryRepairResult Failed(string errorMessage, string? originalReason = null) => new()
    {
        Success = false,
        ErrorMessage = errorMessage,
        OriginalValidationReason = originalReason
    };

    /// <summary>
    /// Creates a result indicating no repair was needed.
    /// </summary>
    public static GeometryRepairResult NotNeeded(byte[] originalWkb) => new()
    {
        Success = true,
        RepairedWkb = originalWkb
    };
}
