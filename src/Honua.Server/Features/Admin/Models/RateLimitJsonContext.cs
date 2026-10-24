// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// JSON source generation context for rate limit admin API models.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ApiResponse<IReadOnlyList<RateLimitPolicyResponse>>))]
[JsonSerializable(typeof(ApiResponse<RateLimitPolicyResponse>))]
[JsonSerializable(typeof(ApiResponse<RateLimitStatusResponse>))]
[JsonSerializable(typeof(ApiResponse<object>))]
[JsonSerializable(typeof(CreateRateLimitPolicyRequest))]
[JsonSerializable(typeof(UpdateRateLimitPolicyRequest))]
[JsonSerializable(typeof(RateLimitPolicyResponse))]
[JsonSerializable(typeof(RateLimitStatusResponse))]
internal sealed partial class RateLimitJsonContext : JsonSerializerContext
{
}
