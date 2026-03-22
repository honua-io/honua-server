// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// JSON source generation context for manifest approval APIs.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ManifestPendingChangeResponse))]
[JsonSerializable(typeof(ManifestPendingChangeResponse[]))]
[JsonSerializable(typeof(ManifestApproveRequest))]
[JsonSerializable(typeof(ManifestRejectRequest))]
[JsonSerializable(typeof(ManifestApprovalWebhookEvent))]
[JsonSerializable(typeof(ApiResponse<ManifestPendingChangeResponse>))]
[JsonSerializable(typeof(ApiResponse<ManifestPendingChangeResponse[]>))]
[JsonSerializable(typeof(ApiResponse<ManifestApplyResult>))]
[JsonSerializable(typeof(ApiResponse<object>))]
public sealed partial class ManifestApprovalJsonContext : JsonSerializerContext
{
}
