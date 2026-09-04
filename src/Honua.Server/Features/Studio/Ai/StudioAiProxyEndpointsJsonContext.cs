// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
internal sealed partial class StudioAiProxyEndpointsJsonContext : JsonSerializerContext
{
}
