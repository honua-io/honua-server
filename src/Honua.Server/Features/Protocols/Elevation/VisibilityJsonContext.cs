// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Protocols.Elevation;

[JsonSerializable(typeof(LineOfSightRequest))]
[JsonSerializable(typeof(LineOfSightResponse))]
[JsonSerializable(typeof(LineOfSightObstructionDto))]
[JsonSerializable(typeof(ViewshedRequest))]
[JsonSerializable(typeof(ViewshedResponse))]
[JsonSerializable(typeof(ViewshedSampleDto))]
[JsonSerializable(typeof(ViewshedSampleDto[]))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
internal sealed partial class VisibilityJsonContext : JsonSerializerContext
{
}
