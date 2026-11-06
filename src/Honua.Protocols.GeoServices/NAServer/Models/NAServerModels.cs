// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.NAServer.Models;

internal sealed class NAServerRouteSolveResponse
{
    public NAServerRouteFeatureSet Routes { get; init; } = new();

    public NAServerDirection[] Directions { get; init; } = [];

    public NAServerMessage[]? Messages { get; init; }
}

internal sealed class NAServerClosestFacilityResponse
{
    public NAServerCfRouteFeatureSet? Routes { get; init; }

    public NAServerDirection[] Directions { get; init; } = [];

    public NAServerMessage[]? Messages { get; init; }
}

internal sealed class NAServerCfRouteFeatureSet
{
    [JsonPropertyName("geometryType")]
    public string GeometryType { get; init; } = "esriGeometryPolyline";

    public NAServerSpatialReference? SpatialReference { get; init; }

    public NAServerCfRouteFeature[] Features { get; init; } = [];
}

internal sealed class NAServerCfRouteFeature
{
    public NAServerPolylineGeometry? Geometry { get; init; }

    public NAServerCfRouteAttributes Attributes { get; init; } = new();
}

internal sealed class NAServerCfRouteAttributes
{
    [JsonPropertyName("Name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("IncidentID")]
    public int IncidentId { get; init; }

    [JsonPropertyName("FacilityID")]
    public int FacilityId { get; init; }

    [JsonPropertyName("FacilityRank")]
    public int FacilityRank { get; init; }

    [JsonPropertyName("Total_Length")]
    public double TotalLength { get; init; }

    [JsonPropertyName("Total_TravelTime")]
    public double TotalTravelTime { get; init; }
}

internal sealed class NAServerOdCostMatrixResponse
{
    public NAServerOdLinesFeatureSet OdLines { get; init; } = new();

    public NAServerMessage[]? Messages { get; init; }
}

internal sealed class NAServerOdLinesFeatureSet
{
    [JsonPropertyName("geometryType")]
    public string? GeometryType { get; init; }

    public NAServerSpatialReference? SpatialReference { get; init; }

    public NAServerOdLineFeature[] Features { get; init; } = [];
}

internal sealed class NAServerOdLineFeature
{
    public NAServerPolylineGeometry? Geometry { get; init; }

    public NAServerOdLineAttributes Attributes { get; init; } = new();
}

internal sealed class NAServerOdLineAttributes
{
    [JsonPropertyName("OriginID")]
    public int OriginId { get; init; }

    [JsonPropertyName("DestinationID")]
    public int DestinationId { get; init; }

    [JsonPropertyName("DestinationRank")]
    public int DestinationRank { get; init; }

    [JsonPropertyName("Total_Time")]
    public double TotalTime { get; init; }

    [JsonPropertyName("Total_Distance")]
    public double TotalDistance { get; init; }
}

internal sealed class NAServerLocationAllocationResponse
{
    public NAServerLaFacilitiesFeatureSet Facilities { get; init; } = new();

    public NAServerLaDemandPointsFeatureSet DemandPoints { get; init; } = new();

    public NAServerMessage[]? Messages { get; init; }
}

internal sealed class NAServerLaFacilitiesFeatureSet
{
    public NAServerLaFacilityFeature[] Features { get; init; } = [];
}

internal sealed class NAServerLaFacilityFeature
{
    public NAServerLaFacilityAttributes Attributes { get; init; } = new();
}

internal sealed class NAServerLaFacilityAttributes
{
    [JsonPropertyName("FacilityID")]
    public int FacilityId { get; init; }

    [JsonPropertyName("FacilityType")]
    public int FacilityType { get; init; } = 3;

    [JsonPropertyName("DemandWeight")]
    public double DemandWeight { get; init; }
}

internal sealed class NAServerLaDemandPointsFeatureSet
{
    public NAServerLaDemandPointFeature[] Features { get; init; } = [];
}

internal sealed class NAServerLaDemandPointFeature
{
    public NAServerLaDemandPointAttributes Attributes { get; init; } = new();
}

internal sealed class NAServerLaDemandPointAttributes
{
    [JsonPropertyName("DemandOID")]
    public int DemandOid { get; init; }

    [JsonPropertyName("FacilityID")]
    public int FacilityId { get; init; }

    [JsonPropertyName("Weight")]
    public double Weight { get; init; }

    [JsonPropertyName("AllocatedTime")]
    public double AllocatedTime { get; init; }
}

internal sealed class NAServerServiceAreaResponse
{
    public NAServerSaPolygonsFeatureSet SaPolygons { get; init; } = new();

    public NAServerMessage[]? Messages { get; init; }
}

internal sealed class NAServerRouteFeatureSet
{
    [JsonPropertyName("geometryType")]
    public string? GeometryType { get; init; }

    public NAServerSpatialReference? SpatialReference { get; init; }

    public NAServerRouteFeature[] Features { get; init; } = [];
}

internal sealed class NAServerRouteFeature
{
    public NAServerPolylineGeometry? Geometry { get; init; }

    public NAServerRouteAttributes Attributes { get; init; } = new();
}

internal sealed class NAServerRouteAttributes
{
    [JsonPropertyName("Name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("Total_Length")]
    public double TotalLength { get; init; }

    [JsonPropertyName("Total_TravelTime")]
    public double TotalTravelTime { get; init; }
}

internal sealed class NAServerDirection
{
    public int RouteId { get; init; }

    public string RouteName { get; init; } = string.Empty;

    public NAServerDirectionSummary? Summary { get; init; }

    public NAServerDirectionFeature[]? Features { get; init; }
}

internal sealed class NAServerDirectionFeature
{
    public NAServerDirectionAttributes Attributes { get; init; } = new();
}

internal sealed class NAServerDirectionAttributes
{
    public string Text { get; init; } = string.Empty;

    public double Length { get; init; }

    public double Time { get; init; }

    public string ManeuverType { get; init; } = string.Empty;
}

internal sealed class NAServerDirectionSummary
{
    public double TotalLength { get; init; }

    public double TotalTime { get; init; }

    public double TotalDriveTime { get; init; }

    public NAServerEnvelope? Envelope { get; init; }
}

internal sealed class NAServerEnvelope
{
    public double Xmin { get; init; }

    public double Ymin { get; init; }

    public double Xmax { get; init; }

    public double Ymax { get; init; }

    public NAServerSpatialReference? SpatialReference { get; init; }
}

internal sealed class NAServerSaPolygonsFeatureSet
{
    [JsonPropertyName("geometryType")]
    public string GeometryType { get; init; } = "esriGeometryPolygon";

    public NAServerSpatialReference? SpatialReference { get; init; }

    public NAServerSaPolygonFeature[] Features { get; init; } = [];
}

internal sealed class NAServerSaPolygonFeature
{
    public NAServerPolygonGeometry? Geometry { get; init; }

    public NAServerSaPolygonAttributes Attributes { get; init; } = new();
}

internal sealed class NAServerSaPolygonAttributes
{
    [JsonPropertyName("ObjectID")]
    public int ObjectId { get; init; }

    [JsonPropertyName("FacilityID")]
    public int FacilityId { get; init; }

    [JsonPropertyName("Name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("FromBreak")]
    public double FromBreak { get; init; }

    [JsonPropertyName("ToBreak")]
    public double ToBreak { get; init; }
}

internal sealed class NAServerPolylineGeometry
{
    public double[][][] Paths { get; init; } = [];

    public NAServerSpatialReference? SpatialReference { get; init; }
}

internal sealed class NAServerPolygonGeometry
{
    public double[][][] Rings { get; init; } = [];

    public NAServerSpatialReference? SpatialReference { get; init; }
}

internal sealed class NAServerSpatialReference
{
    public int Wkid { get; init; }

    public int LatestWkid { get; init; }
}

internal sealed class NAServerMessage
{
    public int Type { get; init; }

    public string Description { get; init; } = string.Empty;
}
