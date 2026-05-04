// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Scene.Domain;

/// <summary>
/// Geometry kinds accepted by the v1 3D Tiles generation pipeline. Multi-part
/// inputs are normalized into single-part tiles using ordered partitioning.
/// </summary>
public enum SceneGeometryKind
{
    /// <summary>A single 3D point.</summary>
    Point,

    /// <summary>A 3D polyline (open or closed).</summary>
    LineString,

    /// <summary>A 3D polygon ring (outer ring; v1 ignores inner rings).</summary>
    Polygon
}

/// <summary>
/// A single ordered vertex on the WGS-84 ellipsoid. Longitude and latitude are
/// in decimal degrees; height is the optional ellipsoidal height in meters.
/// </summary>
public readonly record struct SceneVertex(double Longitude, double Latitude, double? Height);

/// <summary>
/// Geometry payload for a single <see cref="SceneFeature"/>. Vertices are in
/// WGS-84 degrees; the executor projects to ECEF (EPSG:4978) when writing GLB.
/// </summary>
public sealed record SceneFeatureGeometry
{
    /// <summary>Geometry kind.</summary>
    public required SceneGeometryKind Kind { get; init; }

    /// <summary>Ordered ring vertices. For points the list contains a single vertex.</summary>
    public required IReadOnlyList<SceneVertex> Vertices { get; init; }
}

/// <summary>
/// One feature presented to the v1 3D Tiles generator. The <see cref="Id"/> is
/// the layer-scoped object id used for deterministic ordering and feature-id
/// metadata; <see cref="Attributes"/> is the projected attribute bag emitted
/// into the tileset's metadata schema.
/// </summary>
public sealed record SceneFeature
{
    /// <summary>Layer-scoped object id (used for deterministic ordering).</summary>
    public required long Id { get; init; }

    /// <summary>Geometry in WGS-84 degrees.</summary>
    public required SceneFeatureGeometry Geometry { get; init; }

    /// <summary>Attributes projected from the layer (string/number values only).</summary>
    public IReadOnlyDictionary<string, object?> Attributes { get; init; }
        = new Dictionary<string, object?>(StringComparer.Ordinal);
}
