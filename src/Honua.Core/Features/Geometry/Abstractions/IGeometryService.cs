// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Geometry.Abstractions;

/// <summary>
/// Unified service for geometry format conversion, Z/M detection, and validation.
/// Provides consistent behavior across all protocols (GeoServices REST, OGC API Features, OData, MVT).
/// </summary>
/// <remarks>
/// <para>
/// This interface consolidates geometry operations that were previously scattered across
/// multiple implementations with divergent behavior. All protocols should use this service
/// for geometry operations to ensure consistent results.
/// </para>
/// <para>
/// Z/M coordinate detection uses a consistent algorithm: iterate through coordinate sequences
/// checking for non-NaN values, not just the first coordinate.
/// </para>
/// </remarks>
public interface IGeometryService
{
    /// <summary>
    /// Detects whether a WKB geometry contains Z and/or M coordinates.
    /// Uses consistent detection by checking all coordinate sequences for non-NaN values.
    /// </summary>
    /// <param name="wkb">The WKB geometry bytes.</param>
    /// <returns>Tuple indicating presence of Z and M coordinates.</returns>
    (bool HasZ, bool HasM) DetectZM(byte[]? wkb);

    /// <summary>
    /// Detects whether a WKB geometry contains Z and/or M coordinates.
    /// Uses consistent detection by checking all coordinate sequences for non-NaN values.
    /// </summary>
    /// <param name="wkb">The WKB geometry bytes as Memory.</param>
    /// <returns>Tuple indicating presence of Z and M coordinates.</returns>
    (bool HasZ, bool HasM) DetectZM(Memory<byte> wkb);

    /// <summary>
    /// Converts WKB geometry to GeoJSON string format.
    /// </summary>
    /// <param name="wkb">The WKB geometry bytes.</param>
    /// <returns>GeoJSON string representation, or null for null/empty input.</returns>
    /// <exception cref="ArgumentException">Thrown when WKB format is invalid.</exception>
    string? ConvertWkbToGeoJson(byte[]? wkb);

    /// <summary>
    /// Converts WKB geometry to GeoJSON string format using pooled memory.
    /// </summary>
    /// <param name="wkb">The WKB geometry bytes as Memory.</param>
    /// <returns>GeoJSON string representation, or null for empty input.</returns>
    /// <exception cref="ArgumentException">Thrown when WKB format is invalid.</exception>
    string? ConvertWkbToGeoJson(Memory<byte> wkb);

    /// <summary>
    /// Converts GeoJSON string to WKB format.
    /// </summary>
    /// <param name="geoJson">The GeoJSON string.</param>
    /// <param name="srid">Optional SRID to assign to the geometry.</param>
    /// <returns>WKB bytes, or null for null/empty input.</returns>
    /// <exception cref="ArgumentException">Thrown when GeoJSON format is invalid.</exception>
    byte[]? ConvertGeoJsonToWkb(string? geoJson, int? srid = null);

    /// <summary>
    /// Converts WKT (Well-Known Text) to WKB format.
    /// </summary>
    /// <param name="wkt">The WKT string.</param>
    /// <param name="srid">Optional SRID to assign to the geometry.</param>
    /// <returns>WKB bytes, or null for null/empty input.</returns>
    /// <exception cref="ArgumentException">Thrown when WKT format is invalid.</exception>
    byte[]? ConvertWktToWkb(string? wkt, int? srid = null);

    /// <summary>
    /// Gets geometry statistics including type, vertex count, ring count, and Z/M flags.
    /// </summary>
    /// <param name="wkb">The WKB geometry bytes.</param>
    /// <returns>Statistics about the geometry, or null for null/empty input.</returns>
    GeometryInfo? GetGeometryInfo(byte[]? wkb);
}

/// <summary>
/// Information about a geometry including type, dimensions, and statistics.
/// </summary>
public sealed record GeometryInfo
{
    /// <summary>
    /// The geometry type (Point, LineString, Polygon, MultiPoint, etc.).
    /// </summary>
    public required string GeometryType { get; init; }

    /// <summary>
    /// Number of vertices/coordinates in the geometry.
    /// </summary>
    public int VertexCount { get; init; }

    /// <summary>
    /// Number of rings in polygon geometries (exterior + interior rings).
    /// </summary>
    public int RingCount { get; init; }

    /// <summary>
    /// Size of the WKB representation in bytes.
    /// </summary>
    public long WkbSize { get; init; }

    /// <summary>
    /// Whether the geometry contains Z (elevation) coordinates.
    /// </summary>
    public bool HasZ { get; init; }

    /// <summary>
    /// Whether the geometry contains M (measure) values.
    /// </summary>
    public bool HasM { get; init; }

    /// <summary>
    /// The spatial reference ID (SRID) if present in the WKB.
    /// </summary>
    public int? Srid { get; init; }
}
