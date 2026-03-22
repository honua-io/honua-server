// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// JSON source generation context for role management admin API models.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ApiResponse<IReadOnlyList<RoleResponse>>))]
[JsonSerializable(typeof(ApiResponse<RoleResponse>))]
[JsonSerializable(typeof(ApiResponse<IReadOnlyList<PermissionGrantResponse>>))]
[JsonSerializable(typeof(ApiResponse<object>))]
[JsonSerializable(typeof(CreateRoleRequest))]
[JsonSerializable(typeof(UpdateRoleRequest))]
[JsonSerializable(typeof(SetPermissionsRequest))]
[JsonSerializable(typeof(PermissionGrantRequest))]
[JsonSerializable(typeof(RoleResponse))]
[JsonSerializable(typeof(PermissionGrantResponse))]
internal sealed partial class RoleJsonContext : JsonSerializerContext
{
}
