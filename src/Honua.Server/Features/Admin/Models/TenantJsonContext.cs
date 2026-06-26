// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// JSON source generation context for tenant lifecycle admin API models (issue #2156).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ApiResponse<IReadOnlyList<TenantResponse>>))]
[JsonSerializable(typeof(ApiResponse<TenantResponse>))]
[JsonSerializable(typeof(ApiResponse<TenantUsageResponse>))]
[JsonSerializable(typeof(ApiResponse<object>))]
[JsonSerializable(typeof(CreateTenantRequest))]
[JsonSerializable(typeof(TenantResponse))]
[JsonSerializable(typeof(TenantUsageResponse))]
internal sealed partial class TenantJsonContext : JsonSerializerContext
{
}
