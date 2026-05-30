// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// JSON source generation context for admin API key management models.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ApiResponse<IReadOnlyList<AdminApiKeyResponse>>))]
[JsonSerializable(typeof(ApiResponse<AdminApiKeyResponse>))]
[JsonSerializable(typeof(ApiResponse<AdminApiKeySecretResponse>))]
[JsonSerializable(typeof(ApiResponse<AdminApiKeyEffectivePermissionsResponse>))]
[JsonSerializable(typeof(ApiResponse<object>))]
[JsonSerializable(typeof(CreateAdminApiKeyRequest))]
[JsonSerializable(typeof(AdminApiKeyResponse))]
[JsonSerializable(typeof(AdminApiKeySecretResponse))]
[JsonSerializable(typeof(AdminApiKeyEffectivePermissionsResponse))]
internal sealed partial class AdminApiKeyJsonContext : JsonSerializerContext
{
}
