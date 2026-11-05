// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Admin.Routing;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(NetworkTopologyRebuildSubmissionDto))]
[JsonSerializable(typeof(NetworkTopologyRebuildAttemptDto))]
[JsonSerializable(typeof(NetworkTopologyPromotionRequest))]
[JsonSerializable(typeof(NetworkTopologyPromotionDto))]
[JsonSerializable(typeof(NetworkTopologyPromotionDto[]))]
internal sealed partial class NetworkTopologyRebuildAdminJsonContext : JsonSerializerContext
{
}
