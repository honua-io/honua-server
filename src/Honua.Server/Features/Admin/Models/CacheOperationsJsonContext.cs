// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// JSON source generation context for cache operations admin API models.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ApiResponse<CacheHealthResponse>))]
[JsonSerializable(typeof(ApiResponse<CacheStatisticsResponse>))]
[JsonSerializable(typeof(ApiResponse<RedisConnectionMetricsResponse>))]
[JsonSerializable(typeof(ApiResponse<CacheOperationsInvalidationResponse>))]
[JsonSerializable(typeof(CacheHealthResponse))]
[JsonSerializable(typeof(CacheStatisticsResponse))]
[JsonSerializable(typeof(CacheTypeStatisticsResponse))]
[JsonSerializable(typeof(RedisConnectionMetricsResponse))]
[JsonSerializable(typeof(RedisServerInfoResponse))]
[JsonSerializable(typeof(CacheOperationsInvalidationRequest))]
[JsonSerializable(typeof(CacheOperationsInvalidationResponse))]
internal sealed partial class CacheOperationsJsonContext : JsonSerializerContext
{
}
