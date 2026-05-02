// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Protocols.Elevation;

[JsonSerializable(typeof(ElevationValueResponse))]
[JsonSerializable(typeof(ElevationProfileResponse))]
[JsonSerializable(typeof(ElevationProfileSample))]
[JsonSerializable(typeof(ElevationProfileSample[]))]
[JsonSerializable(typeof(ElevationSourceMetadata))]
[JsonSerializable(typeof(long[]))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
internal sealed partial class ElevationJsonContext : JsonSerializerContext
{
}
