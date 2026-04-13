// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Core.Configuration;

/// <summary>
/// Configuration options for geometry validation limits.
/// Used to prevent DoS attacks via malformed or oversized geometry data.
/// </summary>
public sealed class GeometryValidationOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "GeometryValidation";

    /// <summary>
    /// Maximum allowed WKB (Well-Known Binary) size in bytes.
    /// </summary>
    [Range(1024, 50 * 1024 * 1024)] // 1KB to 50MB
    public int MaxWkbSize { get; init; } = 10 * 1024 * 1024; // 10MB default

    /// <summary>
    /// Maximum number of vertices allowed in a geometry.
    /// </summary>
    [Range(10, 1_000_000)]
    public int MaxVertices { get; init; } = 100_000;

    /// <summary>
    /// Maximum number of rings allowed in a polygon geometry.
    /// </summary>
    [Range(1, 10_000)]
    public int MaxRings { get; init; } = 1_000;

    /// <summary>
    /// Whether to enable strict validation.
    /// </summary>
    public bool StrictValidation { get; init; } = true;

    /// <summary>
    /// Whether to validate coordinate precision.
    /// </summary>
    public bool ValidateCoordinatePrecision { get; init; } = false;

    /// <summary>
    /// Maximum coordinate precision digits after decimal point.
    /// </summary>
    [Range(1, 15)]
    public int MaxCoordinatePrecision { get; init; } = 10;
}