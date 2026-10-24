// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Protocols.Terrain;

[JsonSerializable(typeof(TerrainMetadataResponse))]
[JsonSerializable(typeof(TerrainEncodingMetadata))]
[JsonSerializable(typeof(TerrainSourceMetadata))]
[JsonSerializable(typeof(TerrainExtentMetadata))]
[JsonSerializable(typeof(TerrainNoDataMetadata))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(long[]))]
[JsonSerializable(typeof(double[]))]
[JsonSerializable(typeof(int[]))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class TerrainJsonContext : JsonSerializerContext
{
}
