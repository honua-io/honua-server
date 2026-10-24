// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.VectorTileServer.Models;

/// <summary>
/// AOT-compatible source-generated JSON serialization context for VectorTileServer models.
/// </summary>
[JsonSerializable(typeof(VectorTileServerMetadataResponse))]
[JsonSerializable(typeof(VectorTileInfo))]
[JsonSerializable(typeof(VectorTileOrigin))]
[JsonSerializable(typeof(VectorTileSpatialReference))]
[JsonSerializable(typeof(VectorTileLevelOfDetail))]
[JsonSerializable(typeof(VectorTileLevelOfDetail[]))]
[JsonSerializable(typeof(VectorTileExtent))]
[JsonSerializable(typeof(VectorTileMapResponse))]
[JsonSerializable(typeof(VectorTileMapLocation))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class VectorTileServerJsonContext : JsonSerializerContext;
