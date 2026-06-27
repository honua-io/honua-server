// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Admin.EmbedGovernance.Models;

/// <summary>
/// JSON source generation context for embed governance models.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ApiResponse<IReadOnlyList<EmbedKeyResponse>>))]
[JsonSerializable(typeof(ApiResponse<EmbedKeyResponse>))]
[JsonSerializable(typeof(ApiResponse<EmbedKeySecretResponse>))]
[JsonSerializable(typeof(ApiResponse<EmbedUsageResponse>))]
[JsonSerializable(typeof(ApiResponse<EmbedAnalyticsIngestResponse>))]
[JsonSerializable(typeof(ApiResponse<object>))]
[JsonSerializable(typeof(CreateEmbedKeyRequest))]
[JsonSerializable(typeof(EmbedKeyResponse))]
[JsonSerializable(typeof(EmbedKeySecretResponse))]
[JsonSerializable(typeof(EmbedPolicyResponse))]
[JsonSerializable(typeof(IngestEmbedAnalyticsRequest))]
[JsonSerializable(typeof(EmbedAnalyticsEventDto))]
[JsonSerializable(typeof(EmbedUsageResponse))]
internal sealed partial class EmbedGovernanceJsonContext : JsonSerializerContext
{
}
