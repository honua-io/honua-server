// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.GeometryService.Models;

/// <summary>
/// Source-generated JSON serializer context for geometry service types (AOT compatible).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GeometryServiceResponse))]
[JsonSerializable(typeof(GeometryServiceSpatialReference))]
[JsonSerializable(typeof(GeometryServiceAreasAndLengthsResponse))]
[JsonSerializable(typeof(GeometryServiceLengthResponse))]
[JsonSerializable(typeof(GeometryServiceDistanceResponse))]
[JsonSerializable(typeof(GeometryServiceRelationResponse))]
[JsonSerializable(typeof(GeometryServiceGeometryResponse))]
[JsonSerializable(typeof(GeometryServiceLabelPointsResponse))]
[JsonSerializable(typeof(GeometryServiceCutResponse))]
[JsonSerializable(typeof(GeometryServiceFindTransformationsResponse))]
[JsonSerializable(typeof(GeometryServiceTransformation))]
[JsonSerializable(typeof(GeometryServiceToGeoCoordinateStringResponse))]
[JsonSerializable(typeof(GeometryServiceFromGeoCoordinateStringResponse))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class GeometryServiceJsonContext : JsonSerializerContext;
