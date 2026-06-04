// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.NAServer.Models;

/// <summary>
/// NAServer route solve response. Mirrors the ArcGIS NAServer <c>solve</c>
/// envelope consumed by Esri routing clients.
/// </summary>
internal sealed class NAServerRouteSolveResponse
{
    /// <summary>Route feature set.</summary>
    public NAServerRouteFeatureSet Routes { get; init; } = new();

    /// <summary>Turn-by-turn directions.</summary>
    public NAServerDirection[] Directions { get; init; } = [];

    /// <summary>Solver messages (informative / warning / error).</summary>
    public NAServerMessage[]? Messages { get; init; }
}

/// <summary>
/// Minimal NAServer closest facility response.
/// </summary>
internal sealed class NAServerClosestFacilityResponse
{
    /// <summary>Closest-facility route feature set.</summary>
    public NAServerRouteFeatureSet? Routes { get; init; }

    /// <summary>Closest-facility directions.</summary>
    public NAServerDirection[] Directions { get; init; } = [];
}

/// <summary>
/// NAServer service-area solve response carrying the computed coverage polygons.
/// </summary>
internal sealed class NAServerServiceAreaResponse
{
    /// <summary>Service-area polygon feature set.</summary>
    public NAServerSaPolygonsFeatureSet SaPolygons { get; init; } = new();

    /// <summary>Solver messages (informative / warning / error).</summary>
    public NAServerMessage[]? Messages { get; init; }
}

/// <summary>
/// GeoServices feature set carrying route features.
/// </summary>
internal sealed class NAServerRouteFeatureSet
{
    /// <summary>Esri geometry type for the contained features.</summary>
    [JsonPropertyName("geometryType")]
    public string? GeometryType { get; init; }

    /// <summary>Spatial reference of the contained geometries.</summary>
    public NAServerSpatialReference? SpatialReference { get; init; }

    /// <summary>Route features.</summary>
    public NAServerRouteFeature[] Features { get; init; } = [];
}

/// <summary>
/// Route feature wrapper.
/// </summary>
internal sealed class NAServerRouteFeature
{
    /// <summary>Route polyline geometry.</summary>
    public NAServerPolylineGeometry? Geometry { get; init; }

    /// <summary>Route attributes.</summary>
    public NAServerRouteAttributes Attributes { get; init; } = new();
}

/// <summary>
/// Route attributes parsed by Esri routing clients.
/// </summary>
internal sealed class NAServerRouteAttributes
{
    /// <summary>Route name.</summary>
    [JsonPropertyName("Name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Total route length in meters.</summary>
    [JsonPropertyName("Total_Length")]
    public double TotalLength { get; init; }

    /// <summary>Total travel time in minutes.</summary>
    [JsonPropertyName("Total_TravelTime")]
    public double TotalTravelTime { get; init; }
}

/// <summary>
/// Direction result wrapper.
/// </summary>
internal sealed class NAServerDirection
{
    /// <summary>Direction features.</summary>
    public NAServerDirectionFeature[]? Features { get; init; }

    /// <summary>Direction summary.</summary>
    public NAServerDirectionSummary? Summary { get; init; }
}

/// <summary>
/// Direction feature wrapper.
/// </summary>
internal sealed class NAServerDirectionFeature
{
    /// <summary>Direction attributes.</summary>
    public NAServerDirectionAttributes Attributes { get; init; } = new();
}

/// <summary>
/// Turn-by-turn direction attributes parsed by Esri routing clients.
/// </summary>
internal sealed class NAServerDirectionAttributes
{
    /// <summary>Instruction text.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Segment length in meters.</summary>
    public double Length { get; init; }

    /// <summary>Segment travel time in minutes.</summary>
    public double Time { get; init; }

    /// <summary>Esri maneuver type.</summary>
    public string ManeuverType { get; init; } = string.Empty;
}

/// <summary>
/// Closest-facility direction summary.
/// </summary>
internal sealed class NAServerDirectionSummary
{
    /// <summary>Route name.</summary>
    public string RouteName { get; init; } = string.Empty;

    /// <summary>Total length.</summary>
    public double TotalLength { get; init; }

    /// <summary>Total travel time.</summary>
    public double TotalTime { get; init; }
}

/// <summary>
/// Feature set carrying the service-area coverage polygons.
/// </summary>
internal sealed class NAServerSaPolygonsFeatureSet
{
    /// <summary>Esri geometry type for the contained features.</summary>
    [JsonPropertyName("geometryType")]
    public string GeometryType { get; init; } = "esriGeometryPolygon";

    /// <summary>Spatial reference of the contained polygons.</summary>
    public NAServerSpatialReference? SpatialReference { get; init; }

    /// <summary>Service-area polygon features.</summary>
    public NAServerSaPolygonFeature[] Features { get; init; } = [];
}

/// <summary>
/// A single service-area polygon feature.
/// </summary>
internal sealed class NAServerSaPolygonFeature
{
    /// <summary>Polygon geometry.</summary>
    public NAServerPolygonGeometry? Geometry { get; init; }

    /// <summary>Polygon attributes.</summary>
    public NAServerSaPolygonAttributes Attributes { get; init; } = new();
}

/// <summary>
/// Attributes of a service-area polygon parsed by Esri routing clients.
/// </summary>
internal sealed class NAServerSaPolygonAttributes
{
    /// <summary>Object identifier.</summary>
    [JsonPropertyName("ObjectID")]
    public int ObjectId { get; init; }

    /// <summary>Facility identifier the polygon belongs to.</summary>
    [JsonPropertyName("FacilityID")]
    public int FacilityId { get; init; }

    /// <summary>Polygon display name.</summary>
    [JsonPropertyName("Name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Inner break (minutes) of this ring.</summary>
    [JsonPropertyName("FromBreak")]
    public double FromBreak { get; init; }

    /// <summary>Outer break (minutes) of this ring.</summary>
    [JsonPropertyName("ToBreak")]
    public double ToBreak { get; init; }
}

/// <summary>
/// Esri polyline geometry (<c>paths</c>).
/// </summary>
internal sealed class NAServerPolylineGeometry
{
    /// <summary>Path arrays; each path is an array of <c>[x, y]</c> vertices.</summary>
    public double[][][] Paths { get; init; } = [];

    /// <summary>Spatial reference of the path coordinates.</summary>
    public NAServerSpatialReference? SpatialReference { get; init; }
}

/// <summary>
/// Esri polygon geometry (<c>rings</c>).
/// </summary>
internal sealed class NAServerPolygonGeometry
{
    /// <summary>Ring arrays; each ring is an array of <c>[x, y]</c> vertices.</summary>
    public double[][][] Rings { get; init; } = [];

    /// <summary>Spatial reference of the ring coordinates.</summary>
    public NAServerSpatialReference? SpatialReference { get; init; }
}

/// <summary>
/// Esri spatial reference (<c>{ "wkid": ... }</c>).
/// </summary>
internal sealed class NAServerSpatialReference
{
    /// <summary>Well-known spatial reference identifier.</summary>
    public int Wkid { get; init; }

    /// <summary>Latest well-known spatial reference identifier.</summary>
    public int LatestWkid { get; init; }
}

/// <summary>
/// Esri solver message envelope.
/// </summary>
internal sealed class NAServerMessage
{
    /// <summary>Esri message type code.</summary>
    public int Type { get; init; }

    /// <summary>Human-readable message description.</summary>
    public string Description { get; init; } = string.Empty;
}
