// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
