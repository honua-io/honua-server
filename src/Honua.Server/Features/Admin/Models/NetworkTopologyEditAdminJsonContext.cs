// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Source-generated JSON context for the network-topology edit admin API models (#2716).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(NetworkTopologyGenerationDto))]
[JsonSerializable(typeof(NetworkTopologyGenerationDto[]))]
[JsonSerializable(typeof(NetworkTopologyEditBatchRequest))]
[JsonSerializable(typeof(NetworkTopologyEditResultDto))]
internal sealed partial class NetworkTopologyEditAdminJsonContext : JsonSerializerContext
{
}
