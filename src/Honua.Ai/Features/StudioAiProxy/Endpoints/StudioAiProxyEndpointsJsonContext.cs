// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Ai.StudioAiProxy.Domain;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Studio.Ai;

/// <summary>Source-generated JSON context for the Studio AI proxy endpoints' <see cref="ApiResponse{T}"/> envelope.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ApiResponse<StudioAiCapabilitiesResponse>))]
[JsonSerializable(typeof(StudioAiChatHttpRequest))]
internal sealed partial class StudioAiProxyEndpointsJsonContext : JsonSerializerContext
{
}
