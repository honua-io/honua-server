// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.NAServer.Models;

/// <summary>
/// AOT-compatible JSON serialization context for the NAServer wire-format models.
/// </summary>
[JsonSerializable(typeof(NAServerRouteSolveResponse))]
[JsonSerializable(typeof(NAServerClosestFacilityResponse))]
[JsonSerializable(typeof(NAServerServiceAreaResponse))]
[JsonSerializable(typeof(NAServerRouteFeatureSet))]
[JsonSerializable(typeof(NAServerRouteFeature))]
[JsonSerializable(typeof(NAServerRouteFeature[]))]
[JsonSerializable(typeof(NAServerRouteAttributes))]
[JsonSerializable(typeof(NAServerDirection))]
[JsonSerializable(typeof(NAServerDirection[]))]
[JsonSerializable(typeof(NAServerDirectionFeature))]
[JsonSerializable(typeof(NAServerDirectionFeature[]))]
[JsonSerializable(typeof(NAServerDirectionAttributes))]
[JsonSerializable(typeof(NAServerDirectionSummary))]
[JsonSerializable(typeof(NAServerSaPolygonsFeatureSet))]
[JsonSerializable(typeof(NAServerSaPolygonFeature))]
[JsonSerializable(typeof(NAServerSaPolygonFeature[]))]
[JsonSerializable(typeof(NAServerSaPolygonAttributes))]
[JsonSerializable(typeof(NAServerPolylineGeometry))]
[JsonSerializable(typeof(NAServerPolygonGeometry))]
[JsonSerializable(typeof(NAServerSpatialReference))]
[JsonSerializable(typeof(NAServerMessage))]
[JsonSerializable(typeof(NAServerMessage[]))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class NAServerJsonContext : JsonSerializerContext;
