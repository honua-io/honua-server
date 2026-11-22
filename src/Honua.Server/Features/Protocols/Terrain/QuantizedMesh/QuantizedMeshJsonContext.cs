// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Protocols.Terrain.QuantizedMesh;

[JsonSerializable(typeof(QuantizedMeshLayerMetadataResponse))]
[JsonSerializable(typeof(QuantizedMeshTileRangeResponse))]
[JsonSerializable(typeof(QuantizedMeshTileRangeResponse[]))]
[JsonSerializable(typeof(QuantizedMeshTileRangeResponse[][]))]
[JsonSerializable(typeof(TerrainCoverageMetadata))]
[JsonSerializable(typeof(TerrainCapabilitiesMetadata))]
[JsonSerializable(typeof(TerrainCapabilityStatus))]
[JsonSerializable(typeof(TerrainLinkMetadata))]
[JsonSerializable(typeof(TerrainLinkMetadata[]))]
[JsonSerializable(typeof(TerrainSourceMetadata))]
[JsonSerializable(typeof(TerrainExtentMetadata))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(long[]))]
[JsonSerializable(typeof(double[]))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class QuantizedMeshJsonContext : JsonSerializerContext
{
}
