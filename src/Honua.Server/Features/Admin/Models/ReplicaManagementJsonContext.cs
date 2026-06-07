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
[JsonSerializable(typeof(ReplicaConflictSummary))]
[JsonSerializable(typeof(ReplicaConflictSummary[]))]
[JsonSerializable(typeof(ReplicaConflictDetail))]
[JsonSerializable(typeof(ReplicaConflictFieldChange))]
[JsonSerializable(typeof(ReplicaConflictFieldChange[]))]
[JsonSerializable(typeof(ReplicaConflictListResponse))]
[JsonSerializable(typeof(ReplicaConflictResolutionRequest))]
[JsonSerializable(typeof(ReplicaConflictResolutionResponse))]
[JsonSerializable(typeof(ApiResponse<ReplicaConflictListResponse>))]
[JsonSerializable(typeof(ApiResponse<ReplicaConflictDetail>))]
[JsonSerializable(typeof(ApiResponse<ReplicaConflictResolutionResponse>))]
[JsonSerializable(typeof(System.Text.Json.JsonElement))]
public sealed partial class ReplicaManagementJsonContext : JsonSerializerContext
{
}
