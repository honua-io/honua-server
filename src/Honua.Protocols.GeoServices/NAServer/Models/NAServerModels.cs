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
/// NAServer closest-facility solve response. Carries the ranked incident→facility
/// routes (a feature set) plus per-route directions, mirroring the ArcGIS
/// <c>solveClosestFacility</c> envelope.
/// </summary>
internal sealed class NAServerClosestFacilityResponse
{
    /// <summary>Closest-facility route feature set (ranked incident→facility routes).</summary>
    public NAServerCfRouteFeatureSet? Routes { get; init; }

    /// <summary>Closest-facility directions.</summary>
    public NAServerDirection[] Directions { get; init; } = [];

    /// <summary>Solver messages (informative / warning / error).</summary>
    public NAServerMessage[]? Messages { get; init; }
}

/// <summary>
/// Feature set carrying closest-facility routes.
/// </summary>
internal sealed class NAServerCfRouteFeatureSet
{
    /// <summary>Esri geometry type for the contained features.</summary>
    [JsonPropertyName("geometryType")]
    public string GeometryType { get; init; } = "esriGeometryPolyline";

    /// <summary>Spatial reference of the contained geometries.</summary>
    public NAServerSpatialReference? SpatialReference { get; init; }

    /// <summary>Closest-facility route features.</summary>
    public NAServerCfRouteFeature[] Features { get; init; } = [];
}

/// <summary>
/// A single closest-facility route feature (incident→ranked facility).
/// </summary>
internal sealed class NAServerCfRouteFeature
{
    /// <summary>Route polyline geometry.</summary>
    public NAServerPolylineGeometry? Geometry { get; init; }

    /// <summary>Closest-facility route attributes.</summary>
    public NAServerCfRouteAttributes Attributes { get; init; } = new();
}

/// <summary>
/// Attributes of a closest-facility route parsed by Esri routing clients.
/// </summary>
internal sealed class NAServerCfRouteAttributes
{
    /// <summary>Route name.</summary>
    [JsonPropertyName("Name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Incident identifier (1-based, Esri convention).</summary>
    [JsonPropertyName("IncidentID")]
    public int IncidentId { get; init; }

    /// <summary>Facility identifier (1-based, Esri convention).</summary>
    [JsonPropertyName("FacilityID")]
    public int FacilityId { get; init; }

    /// <summary>Rank of the facility for the incident (1 = closest).</summary>
    [JsonPropertyName("FacilityRank")]
    public int FacilityRank { get; init; }

    /// <summary>Total route length in meters.</summary>
    [JsonPropertyName("Total_Length")]
    public double TotalLength { get; init; }

    /// <summary>Total travel time in minutes.</summary>
    [JsonPropertyName("Total_TravelTime")]
    public double TotalTravelTime { get; init; }
}

/// <summary>
/// NAServer OD cost matrix solve response. Carries the origin→destination lines
/// (attribute-only cost cells) mirroring the ArcGIS <c>solveODCostMatrix</c>
/// envelope's <c>odLines</c>.
/// </summary>
internal sealed class NAServerOdCostMatrixResponse
{
    /// <summary>OD cost matrix line feature set.</summary>
    public NAServerOdLinesFeatureSet OdLines { get; init; } = new();

    /// <summary>Solver messages (informative / warning / error).</summary>
    public NAServerMessage[]? Messages { get; init; }
}

/// <summary>
/// Feature set carrying OD cost matrix lines (attribute-only; no geometry in the
/// cost-only output).
/// </summary>
internal sealed class NAServerOdLinesFeatureSet
{
    /// <summary>Esri geometry type (none for cost-only output).</summary>
    [JsonPropertyName("geometryType")]
    public string? GeometryType { get; init; }

    /// <summary>OD line features.</summary>
    public NAServerOdLineFeature[] Features { get; init; } = [];
}

/// <summary>
/// A single OD cost matrix line feature (one origin→destination cell).
/// </summary>
internal sealed class NAServerOdLineFeature
{
    /// <summary>OD line attributes.</summary>
    public NAServerOdLineAttributes Attributes { get; init; } = new();
}

/// <summary>
/// Attributes of an OD cost matrix line parsed by Esri routing clients.
/// </summary>
internal sealed class NAServerOdLineAttributes
{
    /// <summary>Origin identifier (1-based, Esri convention).</summary>
    [JsonPropertyName("OriginID")]
    public int OriginId { get; init; }

    /// <summary>Destination identifier (1-based, Esri convention).</summary>
    [JsonPropertyName("DestinationID")]
    public int DestinationId { get; init; }

    /// <summary>Rank of the destination for the origin (1 = closest).</summary>
    [JsonPropertyName("DestinationRank")]
    public int DestinationRank { get; init; }

    /// <summary>Total travel time in minutes.</summary>
    [JsonPropertyName("Total_Time")]
    public double TotalTime { get; init; }

    /// <summary>Total distance in meters (0 for cost-only output).</summary>
    [JsonPropertyName("Total_Distance")]
    public double TotalDistance { get; init; }
}

/// <summary>
/// NAServer location-allocation solve response. Carries the chosen facilities and
/// the demand-point allocations mirroring the ArcGIS <c>solveLocationAllocation</c>
/// envelope.
/// </summary>
internal sealed class NAServerLocationAllocationResponse
{
    /// <summary>Chosen-facility feature set.</summary>
    public NAServerLaFacilitiesFeatureSet Facilities { get; init; } = new();

    /// <summary>Allocated demand-point feature set.</summary>
    public NAServerLaDemandPointsFeatureSet DemandPoints { get; init; } = new();

    /// <summary>Solver messages (informative / warning / error).</summary>
    public NAServerMessage[]? Messages { get; init; }
}

/// <summary>
/// Feature set carrying the chosen location-allocation facilities.
/// </summary>
internal sealed class NAServerLaFacilitiesFeatureSet
{
    /// <summary>Chosen-facility features.</summary>
    public NAServerLaFacilityFeature[] Features { get; init; } = [];
}

/// <summary>
/// A single chosen location-allocation facility feature.
/// </summary>
internal sealed class NAServerLaFacilityFeature
{
    /// <summary>Facility attributes.</summary>
    public NAServerLaFacilityAttributes Attributes { get; init; } = new();
}

/// <summary>
/// Attributes of a chosen location-allocation facility.
/// </summary>
internal sealed class NAServerLaFacilityAttributes
{
    /// <summary>Facility identifier (1-based, Esri convention).</summary>
    [JsonPropertyName("FacilityID")]
    public int FacilityId { get; init; }

    /// <summary>Whether the facility was chosen by the solver (always 1 here).</summary>
    [JsonPropertyName("FacilityType")]
    public int FacilityType { get; init; } = 3;

    /// <summary>Total demand weight allocated to this facility.</summary>
    [JsonPropertyName("DemandWeight")]
    public double DemandWeight { get; init; }
}

/// <summary>
/// Feature set carrying the allocated demand points.
/// </summary>
internal sealed class NAServerLaDemandPointsFeatureSet
{
    /// <summary>Allocated demand-point features.</summary>
    public NAServerLaDemandPointFeature[] Features { get; init; } = [];
}

/// <summary>
/// A single allocated demand-point feature.
/// </summary>
internal sealed class NAServerLaDemandPointFeature
{
    /// <summary>Demand-point attributes.</summary>
    public NAServerLaDemandPointAttributes Attributes { get; init; } = new();
}

/// <summary>
/// Attributes of an allocated demand point parsed by Esri routing clients.
/// </summary>
internal sealed class NAServerLaDemandPointAttributes
{
    /// <summary>Demand-point identifier (1-based, Esri convention).</summary>
    [JsonPropertyName("DemandOID")]
    public int DemandOid { get; init; }

    /// <summary>
    /// Identifier of the facility the demand point is allocated to (1-based), or 0
    /// when unallocated.
    /// </summary>
    [JsonPropertyName("FacilityID")]
    public int FacilityId { get; init; }

    /// <summary>Demand weight.</summary>
    [JsonPropertyName("Weight")]
    public double Weight { get; init; }

    /// <summary>
    /// Allocated impedance (minutes) to the facility, or -1 when unallocated.
    /// </summary>
    [JsonPropertyName("AllocatedTime")]
    public double AllocatedTime { get; init; }
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
