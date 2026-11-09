// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.GeometryService.Models;

public sealed class GeometryServiceResponse
{
    [JsonPropertyName("geometryType")]
    public string? GeometryType { get; init; }

    [JsonPropertyName("spatialReference")]
    public GeometryServiceSpatialReference? SpatialReference { get; init; }

    [JsonPropertyName("geometries")]
    public JsonElement[]? Geometries { get; init; }
}

public sealed class GeometryServiceAreasAndLengthsResponse
{
    [JsonPropertyName("areas")]
    public double[]? Areas { get; init; }

    [JsonPropertyName("lengths")]
    public double[]? Lengths { get; init; }
}

public sealed class GeometryServiceLengthResponse
{
    [JsonPropertyName("lengths")]
    public double[]? Lengths { get; init; }
}

public sealed class GeometryServiceDistanceResponse
{
    [JsonPropertyName("distance")]
    public double Distance { get; init; }
}

public sealed class GeometryServiceRelationResponse
{
    [JsonPropertyName("relations")]
    public GeometryServiceRelationPair[]? Relations { get; init; }
}

public sealed class GeometryServiceRelationPair
{
    [JsonPropertyName("geometry1Index")]
    public int Geometry1Index { get; init; }

    [JsonPropertyName("geometry2Index")]
    public int Geometry2Index { get; init; }
}

public sealed class GeometryServiceGeometryResponse
{
    [JsonPropertyName("geometry")]
    public JsonElement Geometry { get; init; }
}

public sealed class GeometryServiceLabelPointsResponse
{
    [JsonPropertyName("labelPoints")]
    public JsonElement[]? LabelPoints { get; init; }
}

public sealed class GeometryServiceCutResponse
{
    [JsonPropertyName("geometries")]
    public JsonElement[]? Geometries { get; init; }

    [JsonPropertyName("cutIndexes")]
    public int[]? CutIndexes { get; init; }
}

public sealed class GeometryServiceFindTransformationsResponse
{
    [JsonPropertyName("transformations")]
    public GeometryServiceTransformation[]? Transformations { get; init; }
}

public sealed class GeometryServiceTransformation
{
    [JsonPropertyName("wkid")]
    public int? Wkid { get; init; }

    [JsonPropertyName("latestWkid")]
    public int? LatestWkid { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed class GeometryServiceToGeoCoordinateStringResponse
{
    [JsonPropertyName("strings")]
    public string[]? Strings { get; init; }
}

public sealed class GeometryServiceFromGeoCoordinateStringResponse
{
    [JsonPropertyName("coordinates")]
    public double[][]? Coordinates { get; init; }
}

public sealed class GeometryServiceSpatialReference
{
    [JsonPropertyName("wkid")]
    public int Wkid { get; init; }

    [JsonPropertyName("latestWkid")]
    public int LatestWkid { get; init; }
}
