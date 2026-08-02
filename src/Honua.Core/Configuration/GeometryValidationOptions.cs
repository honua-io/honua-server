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
    public int MaxWkbSize { get; set; } = 10 * 1024 * 1024; // 10MB default

    /// <summary>
    /// Maximum number of vertices allowed in a geometry.
    /// </summary>
    [Range(10, 1_000_000)]
    public int MaxVertices { get; set; } = 100_000;

    /// <summary>
    /// Maximum number of rings allowed in a polygon geometry.
    /// </summary>
    [Range(1, 10_000)]
    public int MaxRings { get; set; } = 1_000;

    /// <summary>
    /// Whether to enable strict validation.
    /// </summary>
    public bool StrictValidation { get; set; } = true;

    /// <summary>
    /// Whether to validate coordinate precision.
    /// </summary>
    public bool ValidateCoordinatePrecision { get; set; }

    /// <summary>
    /// Maximum coordinate precision digits after decimal point.
    /// </summary>
    [Range(1, 15)]
    public int MaxCoordinatePrecision { get; set; } = 10;

    /// <summary>
    /// Whether null or empty geometries are accepted.
    /// </summary>
    public bool AllowNullGeometry { get; set; }

    /// <summary>
    /// Whether topology validation is enabled.
    /// </summary>
    public bool EnableTopologyValidation { get; set; } = true;

    /// <summary>
    /// Whether invalid geometries may be repaired automatically.
    /// </summary>
    public bool EnableAutoRepair { get; set; } = true;

    /// <summary>
    /// Validation mode for geometry processing.
    /// </summary>
    public ValidationMode Mode { get; set; } = ValidationMode.Repair;

    /// <summary>
    /// Maximum time allowed for topology validation or repair.
    /// </summary>
    public TimeSpan ValidationTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Tolerance used when checking ring closure.
    /// </summary>
    [Range(0d, 1d)]
    public double RingClosureTolerance { get; set; } = 1e-9;
}

/// <summary>
/// Controls how invalid geometry is handled during validation.
/// </summary>
public enum ValidationMode
{
    /// <summary>
    /// Accept geometry after structural validation only.
    /// </summary>
    Accept,

    /// <summary>
    /// Reject geometry that fails topology validation.
    /// </summary>
    Strict,

    /// <summary>
    /// Attempt to repair geometry that fails topology validation.
    /// </summary>
    Repair
}
