// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

/// <summary>
/// Selects one real-world coordinate on a named non-spatial dimension.
/// </summary>
/// <param name="Variable">Optional variable name; the primary variable is used when omitted.</param>
/// <param name="Dimension">Dimension name as declared by the selected variable.</param>
/// <param name="Coordinate">Real-world coordinate value, or epoch milliseconds for time axes.</param>
public sealed record ZarrPointSliceSelection(string? Variable, string Dimension, double Coordinate);

/// <summary>
/// One point read within a bounded request-scoped Zarr slice batch.
/// </summary>
/// <param name="X">Point X coordinate in <paramref name="InputSrid"/>.</param>
/// <param name="Y">Point Y coordinate in <paramref name="InputSrid"/>.</param>
/// <param name="InputSrid">Optional point spatial reference identifier.</param>
/// <param name="Selections">Coordinate selections that pin non-spatial dimensions.</param>
public sealed record ZarrPointSliceReadRequest(
    double X,
    double Y,
    int? InputSrid,
    IReadOnlyList<ZarrPointSliceSelection> Selections);

/// <summary>
/// Stable outcome categories for a canonical Zarr point-slice read.
/// </summary>
public enum ZarrPointSliceReadStatus
{
    /// <summary>The point was read successfully.</summary>
    Success,

    /// <summary>No scanned Zarr registration exists for the layer.</summary>
    RegistrationNotFound,

    /// <summary>The registration's storage provider has no configured range reader.</summary>
    ReaderUnavailable,

    /// <summary>The variable, dimension, coordinate, or request shape is invalid.</summary>
    InvalidSelection,

    /// <summary>The requested point lies outside the coverage extent.</summary>
    OutsideCoverage,

    /// <summary>The bounded backing-store read failed.</summary>
    ReadFailed,
}

/// <summary>
/// Result of a canonical point read from a selected multidimensional slice.
/// </summary>
/// <param name="Status">Stable outcome category.</param>
/// <param name="Value">Decoded numeric value when successful.</param>
/// <param name="Variable">Resolved variable name when known.</param>
/// <param name="Error">Curated client-safe error detail when unsuccessful.</param>
public readonly record struct ZarrPointSliceReadResult(
    ZarrPointSliceReadStatus Status,
    double? Value,
    string? Variable,
    string? Error);
