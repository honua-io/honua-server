// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.ImageServer.Models;

public sealed class CatalogQueryResponse
{
    [JsonPropertyName("objectIdFieldName")]
    public string ObjectIdFieldName { get; init; } = "OBJECTID";

    [JsonPropertyName("globalIdFieldName")]
    public string GlobalIdFieldName { get; init; } = string.Empty;

    [JsonPropertyName("geometryType")]
    public string GeometryType { get; init; } = "esriGeometryPolygon";

    [JsonPropertyName("spatialReference")]
    public required SpatialReference SpatialReference { get; init; }

    [JsonPropertyName("fields")]
    public Field[] Fields { get; init; } = [];

    [JsonPropertyName("features")]
    public CatalogQueryFeature[] Features { get; init; } = [];

    [JsonPropertyName("exceededTransferLimit")]
    public bool ExceededTransferLimit { get; init; }
}

public sealed class CatalogQueryFeature
{
    [JsonPropertyName("attributes")]
    public Dictionary<string, object?> Attributes { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("geometry")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CatalogQueryGeometry? Geometry { get; init; }
}

public sealed class CatalogQueryGeometry
{
    [JsonPropertyName("rings")]
    public required double[][][] Rings { get; init; }

    [JsonPropertyName("spatialReference")]
    public required SpatialReference SpatialReference { get; init; }
}

public sealed class CatalogObjectIdsResponse
{
    [JsonPropertyName("objectIdFieldName")]
    public string ObjectIdFieldName { get; init; } = "OBJECTID";

    [JsonPropertyName("objectIds")]
    public long[] ObjectIds { get; init; } = [];
}

public sealed class CatalogCountResponse
{
    [JsonPropertyName("count")]
    public long Count { get; init; }
}

public sealed class CatalogExtentResponse
{
    [JsonPropertyName("extent")]
    public required ImageServerExtent Extent { get; init; }
}
