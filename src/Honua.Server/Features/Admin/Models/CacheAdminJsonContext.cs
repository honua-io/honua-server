// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// JSON source generation context for cache admin API models.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ApiResponse<CacheStatusResponse>))]
[JsonSerializable(typeof(ApiResponse<CacheInvalidationResponse>))]
[JsonSerializable(typeof(ApiResponse<object>))]
[JsonSerializable(typeof(CacheStatusResponse))]
[JsonSerializable(typeof(CacheInvalidationRequest))]
[JsonSerializable(typeof(CacheInvalidationResponse))]
internal sealed partial class CacheAdminJsonContext : JsonSerializerContext
{
}
