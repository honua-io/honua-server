// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.MapServer.Models;

/// <summary>
/// AOT-compatible JSON serialization context for MapServer models.
/// </summary>
[JsonSerializable(typeof(MapServerResponse))]
[JsonSerializable(typeof(MapServerLayerInfo))]
[JsonSerializable(typeof(MapServerLayerInfo[]))]
[JsonSerializable(typeof(IdentifyResponse))]
[JsonSerializable(typeof(IdentifyResult))]
[JsonSerializable(typeof(IdentifyResult[]))]
[JsonSerializable(typeof(LegendResponse))]
[JsonSerializable(typeof(LegendLayerInfo))]
[JsonSerializable(typeof(LegendLayerInfo[]))]
[JsonSerializable(typeof(LegendEntry))]
[JsonSerializable(typeof(LegendEntry[]))]
[JsonSerializable(typeof(ExportImageResponse))]
[JsonSerializable(typeof(EsriSpatialReference))]
[JsonSerializable(typeof(EsriExtent))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class MapServerJsonContext : JsonSerializerContext;
