// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Source-generated JSON context for the network-dataset admin API models (#1882).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(NetworkDatasetDto))]
[JsonSerializable(typeof(NetworkDatasetDto[]))]
[JsonSerializable(typeof(IReadOnlyList<NetworkDatasetDto>))]
[JsonSerializable(typeof(RegisterNetworkDatasetRequest))]
[JsonSerializable(typeof(UpdateNetworkDatasetRequest))]
internal sealed partial class NetworkDatasetAdminJsonContext : JsonSerializerContext
{
}
