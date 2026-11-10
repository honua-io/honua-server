// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

public sealed class GeoServicesGeometry
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    [JsonPropertyName("hasZ")]
    public bool HasZ { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    [JsonPropertyName("hasM")]
    public bool HasM { get; init; }

    public double? X { get; init; }

    public double? Y { get; init; }

    public double? Z { get; init; }

    public double? M { get; init; }

    [JsonPropertyName("xmin")]
    public double? Xmin { get; init; }

    [JsonPropertyName("ymin")]
    public double? Ymin { get; init; }

    [JsonPropertyName("xmax")]
    public double? Xmax { get; init; }

    [JsonPropertyName("ymax")]
    public double? Ymax { get; init; }

    [JsonConverter(typeof(GeoServicesVertexListJsonConverter))]
    public double[][]? Points { get; init; }

    [JsonConverter(typeof(GeoServicesVertexListsJsonConverter))]
    public double[][][]? Paths { get; init; }

    [JsonConverter(typeof(GeoServicesVertexListsJsonConverter))]
    public double[][][]? Rings { get; init; }

    // True-curve path elements stay raw JSON so mixed vertex and segment shapes round-trip and remain valid for the source-generated serializer.
    [JsonPropertyName("curvePaths")]
    public JsonElement[][]? CurvePaths { get; init; }

    [JsonPropertyName("curveRings")]
    public JsonElement[][]? CurveRings { get; init; }

    public GeoServicesSpatialReference? SpatialReference { get; init; }
}
