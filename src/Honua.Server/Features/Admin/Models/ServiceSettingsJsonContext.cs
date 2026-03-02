// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// JSON source generation context for service settings admin API models.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ApiResponse<ServiceSettingsResponse>))]
[JsonSerializable(typeof(ApiResponse<ServiceSummary[]>))]
[JsonSerializable(typeof(ApiResponse<LayerMetadataResponse>))]
[JsonSerializable(typeof(ApiResponse<object>))]
[JsonSerializable(typeof(ServiceSettingsResponse))]
[JsonSerializable(typeof(MapServerSettingsResponse))]
[JsonSerializable(typeof(AccessPolicyResponse))]
[JsonSerializable(typeof(TimeInfoResponse))]
[JsonSerializable(typeof(UpdateProtocolsRequest))]
[JsonSerializable(typeof(UpdateMapServerSettingsRequest))]
[JsonSerializable(typeof(UpdateAccessPolicyRequest))]
[JsonSerializable(typeof(UpdateTimeInfoRequest))]
[JsonSerializable(typeof(LayerMetadataResponse))]
[JsonSerializable(typeof(UpdateLayerMetadataRequest))]
[JsonSerializable(typeof(ServiceSummary))]
[JsonSerializable(typeof(ServiceSummary[]))]
internal sealed partial class ServiceSettingsJsonContext : JsonSerializerContext
{
}
