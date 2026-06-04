// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Scene.Bim;

/// <summary>
/// In-memory representation of a parsed CityGML building model with Building
/// Scene Layer (BSL) semantics (#1207). The model preserves the
/// building &#8594; storey &#8594; component (boundary surface) hierarchy and the
/// generic/CityGML attributes attached at each level so the tiling stage can
/// emit them as per-feature metadata.
/// </summary>
/// <remarks>
/// <para>
/// The model is intentionally format-neutral above the CityGML reader: it holds
/// only ordered geometry (planar polygon rings in the source CRS) plus a flat
/// attribute bag per component. The Building Scene Layer "sub-layer / discipline"
/// structure is carried as the <see cref="CityGmlBoundarySurface.Discipline"/>
/// classification (Architectural, Structural, ...) and the
/// <see cref="CityGmlBoundarySurface.SurfaceType"/> (WallSurface, RoofSurface,
/// GroundSurface, ...), which the builder surfaces as BSL attributes.
/// </para>
/// </remarks>
public sealed record CityGmlModel
{
    /// <summary>Source CRS identifier (e.g. <c>urn:ogc:def:crs:EPSG::4979</c>) when declared, else null.</summary>
    public string? SourceCrs { get; init; }

    /// <summary>Whether the source CRS axis order is latitude/longitude (EPSG geographic default) rather than longitude/latitude.</summary>
    public bool LatitudeFirst { get; init; }

    /// <summary>Ordered buildings parsed from the document.</summary>
    public required IReadOnlyList<CityGmlBuilding> Buildings { get; init; }
}

/// <summary>
/// One CityGML building (<c>bldg:Building</c> or a nested <c>bldg:BuildingPart</c>
/// flattened into the same building) with its storey breakdown and the
/// generic/CityGML attributes parsed from the source.
/// </summary>
public sealed record CityGmlBuilding
{
    /// <summary>Stable building identifier (<c>gml:id</c>) used for BSL grouping and feature ids.</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable name (<c>gml:name</c>) when present.</summary>
    public string? Name { get; init; }

    /// <summary>Declared number of storeys above ground, when present.</summary>
    public int? StoreysAboveGround { get; init; }

    /// <summary>Declared number of storeys below ground, when present.</summary>
    public int? StoreysBelowGround { get; init; }

    /// <summary>Parsed generic/CityGML attributes attached at the building level.</summary>
    public IReadOnlyDictionary<string, string> Attributes { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Ordered storeys (BSL sub-layer level). Buildings without explicit storeys carry a single synthetic storey.</summary>
    public required IReadOnlyList<CityGmlStorey> Storeys { get; init; }
}

/// <summary>
/// A storey grouping within a building (the BSL "level" sub-layer). CityGML 2.0
/// has no first-class storey class, so storeys are derived from
/// <c>bldg:Room</c> grouping or synthesised as a single whole-building storey
/// when no room decomposition exists.
/// </summary>
public sealed record CityGmlStorey
{
    /// <summary>Stable storey identifier used for BSL grouping and feature ids.</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable storey name when present.</summary>
    public string? Name { get; init; }

    /// <summary>Ordered boundary surfaces (BSL components) within the storey.</summary>
    public required IReadOnlyList<CityGmlBoundarySurface> Surfaces { get; init; }
}

/// <summary>
/// A single boundary surface (BSL component): a planar polygon ring classified
/// by CityGML surface type and mapped to a BIM discipline / sub-layer.
/// </summary>
public sealed record CityGmlBoundarySurface
{
    /// <summary>Stable surface identifier (<c>gml:id</c>) used for feature ids.</summary>
    public required string Id { get; init; }

    /// <summary>CityGML surface type (e.g. <c>WallSurface</c>, <c>RoofSurface</c>, <c>GroundSurface</c>).</summary>
    public required string SurfaceType { get; init; }

    /// <summary>BIM discipline / Building Scene Layer sub-layer (e.g. <c>Architectural</c>, <c>Structural</c>).</summary>
    public required string Discipline { get; init; }

    /// <summary>Parsed generic attributes attached at the surface level.</summary>
    public IReadOnlyDictionary<string, string> Attributes { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Ordered exterior ring vertices (source CRS, 3D). Inner rings are ignored in this slice.</summary>
    public required IReadOnlyList<CityGmlVertex> Ring { get; init; }
}

/// <summary>
/// A single 3D vertex in the source CRS units (degrees or projected meters,
/// depending on the document CRS), with an elevation in meters.
/// </summary>
/// <param name="X">First ordinate (longitude or easting, per source CRS).</param>
/// <param name="Y">Second ordinate (latitude or northing, per source CRS).</param>
/// <param name="Z">Elevation in meters.</param>
public readonly record struct CityGmlVertex(double X, double Y, double Z);
