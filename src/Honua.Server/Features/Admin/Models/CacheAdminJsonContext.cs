// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Infrastructure.Models;

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
