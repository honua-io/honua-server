// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// JSON source-generation context for the admin replica-management APIs (AOT-safe).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ReplicaManagementSummary))]
[JsonSerializable(typeof(ReplicaManagementSummary[]))]
[JsonSerializable(typeof(ReplicaManagementDetail))]
[JsonSerializable(typeof(ReplicaManagementListResponse))]
[JsonSerializable(typeof(ApiResponse<ReplicaManagementListResponse>))]
[JsonSerializable(typeof(ApiResponse<ReplicaManagementDetail>))]
public sealed partial class ReplicaManagementJsonContext : JsonSerializerContext
{
}
