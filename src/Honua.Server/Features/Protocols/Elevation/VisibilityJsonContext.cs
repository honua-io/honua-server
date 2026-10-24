// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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
