// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.OgcMaps.Models;

/// <summary>
/// JSON serialization context for OGC API - Maps models.
/// Enables AOT-compatible JSON serialization for OGC Maps endpoints.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OgcMapsConformance))]
[JsonSerializable(typeof(OgcMapRequest))]
[JsonSerializable(typeof(MapResponse))]
[JsonSerializable(typeof(MapExtent))]
[JsonSerializable(typeof(SpatialExtent))]
[JsonSerializable(typeof(TemporalExtent))]
[JsonSerializable(typeof(OgcLink))]
[JsonSerializable(typeof(MapCollectionInfo))]
[JsonSerializable(typeof(MapStyle))]
[JsonSerializable(typeof(DatasetMap))]
[JsonSerializable(typeof(TileSet))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(double[][]))]
[JsonSerializable(typeof(string[][]))]
internal sealed partial class OgcMapsJsonContext : JsonSerializerContext
{
}
