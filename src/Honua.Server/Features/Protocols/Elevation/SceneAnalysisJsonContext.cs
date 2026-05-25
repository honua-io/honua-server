// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Protocols.Elevation;

[JsonSerializable(typeof(SunShadowRequest))]
[JsonSerializable(typeof(SunShadowResponse))]
[JsonSerializable(typeof(SolarPositionDto))]
[JsonSerializable(typeof(ShadowSampleDto))]
[JsonSerializable(typeof(ShadowSampleDto[]))]
[JsonSerializable(typeof(SliceRequest))]
[JsonSerializable(typeof(SliceResponse))]
[JsonSerializable(typeof(SliceSampleDto))]
[JsonSerializable(typeof(SliceSampleDto[]))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
internal sealed partial class SceneAnalysisJsonContext : JsonSerializerContext
{
}
