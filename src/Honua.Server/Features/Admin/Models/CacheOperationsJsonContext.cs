// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// JSON source generation context for cache operations admin API models.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ApiResponse<CacheHealthResponse>))]
[JsonSerializable(typeof(ApiResponse<CacheOperationsInvalidationResponse>))]
[JsonSerializable(typeof(CacheHealthResponse))]
[JsonSerializable(typeof(RedisServerInfoResponse))]
[JsonSerializable(typeof(CacheOperationsInvalidationRequest))]
[JsonSerializable(typeof(CacheOperationsInvalidationResponse))]
internal sealed partial class CacheOperationsJsonContext : JsonSerializerContext
{
}
