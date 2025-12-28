// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.Geometry.Domain;

/// <summary>
/// Result of geometry validation operations.
/// </summary>
public sealed record GeometryValidationResult
{
    /// <summary>
    /// Whether the geometry passed validation.
    /// </summary>
    public required bool IsValid { get; init; }

    /// <summary>
    /// List of validation errors (critical issues that prevent processing).
    /// </summary>
    public ImmutableList<ValidationIssue> Errors { get; init; } = ImmutableList<ValidationIssue>.Empty;

    /// <summary>
    /// List of validation warnings (non-critical issues that were handled).
    /// </summary>
    public ImmutableList<ValidationIssue> Warnings { get; init; } = ImmutableList<ValidationIssue>.Empty;

    /// <summary>
    /// The repaired geometry WKB bytes, if repairs were applied.
    /// </summary>
    public byte[]? RepairedWkb { get; init; }

    /// <summary>
    /// Whether repairs were applied to the geometry.
    /// </summary>
    public bool WasRepaired => RepairedWkb != null;

    /// <summary>
    /// Statistics about the validated geometry.
    /// </summary>
    public GeometryStats? Stats { get; init; }

    /// <summary>
    /// Creates a successful validation result.
    /// </summary>
    public static GeometryValidationResult Success(GeometryStats? stats = null) => new()
    {
        IsValid = true,
        Stats = stats
    };

    /// <summary>
    /// Creates a successful validation result with repaired geometry.
    /// </summary>
    public static GeometryValidationResult SuccessWithRepair(byte[] repairedWkb, IEnumerable<ValidationIssue>? warnings = null) => new()
    {
        IsValid = true,
        RepairedWkb = repairedWkb,
        Warnings = warnings?.ToImmutableList() ?? ImmutableList<ValidationIssue>.Empty
    };

    /// <summary>
    /// Creates a failed validation result with errors.
    /// </summary>
    public static GeometryValidationResult Failure(params ValidationIssue[] errors) => new()
    {
        IsValid = false,
        Errors = errors.ToImmutableList()
    };

    /// <summary>
    /// Creates a failed validation result with a single error message.
    /// </summary>
    public static GeometryValidationResult Failure(ValidationErrorCode code, string message) => new()
    {
        IsValid = false,
        Errors = ImmutableList.Create(new ValidationIssue(code, message))
    };

    /// <summary>
    /// Creates a validation result for null geometry.
    /// </summary>
    public static GeometryValidationResult NullGeometry(bool isAllowed) => isAllowed
        ? Success()
        : Failure(ValidationErrorCode.NullGeometry, "Geometry cannot be null");
}

/// <summary>
/// A validation issue (error or warning) with code and message.
/// </summary>
/// <param name="Code">The validation error code.</param>
/// <param name="Message">Human-readable error message.</param>
public sealed record ValidationIssue(ValidationErrorCode Code, string Message);

/// <summary>
/// Statistics about a validated geometry.
/// </summary>
public sealed record GeometryStats
{
    /// <summary>
    /// The geometry type (Point, LineString, Polygon, etc.).
    /// </summary>
    public required string GeometryType { get; init; }

    /// <summary>
    /// Number of vertices in the geometry.
    /// </summary>
    public int VertexCount { get; init; }

    /// <summary>
    /// Number of rings in polygon geometries.
    /// </summary>
    public int RingCount { get; init; }

    /// <summary>
    /// Size of the WKB representation in bytes.
    /// </summary>
    public long WkbSize { get; init; }

    /// <summary>
    /// Whether the geometry has Z coordinates.
    /// </summary>
    public bool HasZ { get; init; }

    /// <summary>
    /// Whether the geometry has M (measure) values.
    /// </summary>
    public bool HasM { get; init; }

    /// <summary>
    /// The SRID (spatial reference ID) of the geometry.
    /// </summary>
    public int? Srid { get; init; }
}

/// <summary>
/// Validation error codes for geometry validation.
/// </summary>
public enum ValidationErrorCode
{
    /// <summary>No error.</summary>
    None = 0,

    // Input validation errors (1xx)
    /// <summary>Geometry is null when not allowed.</summary>
    NullGeometry = 100,
    /// <summary>Invalid JSON structure.</summary>
    InvalidJsonStructure = 101,
    /// <summary>Missing required coordinates.</summary>
    MissingCoordinates = 102,
    /// <summary>Invalid coordinate values (NaN, Infinity).</summary>
    InvalidCoordinates = 103,
    /// <summary>Coordinate out of valid range.</summary>
    CoordinateOutOfRange = 104,
    /// <summary>Ring not properly closed.</summary>
    UnclosedRing = 105,
    /// <summary>Polygon has fewer than 4 points.</summary>
    InsufficientPoints = 106,

    // Security limits (2xx)
    /// <summary>Geometry exceeds maximum vertex count.</summary>
    TooManyVertices = 200,
    /// <summary>Polygon exceeds maximum ring count.</summary>
    TooManyRings = 201,
    /// <summary>WKB size exceeds maximum allowed.</summary>
    WkbTooLarge = 202,
    /// <summary>Validation timeout exceeded.</summary>
    ValidationTimeout = 203,
    /// <summary>Attribute value too long.</summary>
    AttributeTooLong = 204,
    /// <summary>Payload size exceeds maximum allowed.</summary>
    PayloadTooLarge = 205,

    // WKB validation errors (3xx)
    /// <summary>Invalid WKB binary structure.</summary>
    InvalidWkbStructure = 300,
    /// <summary>Unsupported geometry type in WKB.</summary>
    UnsupportedGeometryType = 301,
    /// <summary>SRID mismatch in WKB.</summary>
    SridMismatch = 302,
    /// <summary>WKB is empty.</summary>
    EmptyGeometry = 303,

    // Topology errors (4xx)
    /// <summary>Geometry is topologically invalid.</summary>
    InvalidTopology = 400,
    /// <summary>Self-intersecting polygon.</summary>
    SelfIntersection = 401,
    /// <summary>Duplicate consecutive points.</summary>
    DuplicatePoints = 402,
    /// <summary>Ring has incorrect orientation.</summary>
    IncorrectOrientation = 403,
    /// <summary>Hole outside shell.</summary>
    HoleOutsideShell = 404,
    /// <summary>Nested holes.</summary>
    NestedHoles = 405,
    /// <summary>Disconnected interior.</summary>
    DisconnectedInterior = 406,

    // Repair errors (5xx)
    /// <summary>Geometry repair failed.</summary>
    RepairFailed = 500,
    /// <summary>Repair resulted in empty geometry.</summary>
    RepairResultedInEmpty = 501,
    /// <summary>Repair changed geometry type.</summary>
    RepairChangedType = 502
}
