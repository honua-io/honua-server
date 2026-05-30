// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// JSON source generation context for user management admin API models.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ApiResponse<UserListResponse>))]
[JsonSerializable(typeof(ApiResponse<UserResponse>))]
[JsonSerializable(typeof(ApiResponse<EffectivePermissionsResponse>))]
[JsonSerializable(typeof(ApiResponse<object>))]
[JsonSerializable(typeof(UpdateUserRolesRequest))]
[JsonSerializable(typeof(UserListResponse))]
[JsonSerializable(typeof(UserResponse))]
[JsonSerializable(typeof(EffectivePermissionsResponse))]
[JsonSerializable(typeof(PermissionGrantResponse))]
internal sealed partial class UserManagementJsonContext : JsonSerializerContext
{
}
