// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class VectorTileServerJsonContext : JsonSerializerContext;
