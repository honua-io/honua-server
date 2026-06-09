// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Infrastructure.Models;

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
[JsonSerializable(typeof(ApiResponse<ServiceSettingsCapsResponse>))]
[JsonSerializable(typeof(ServiceSettingsCapsResponse))]
[JsonSerializable(typeof(UpdateServiceSettingsCapsRequest))]
[JsonSerializable(typeof(ApiResponse<DiscoveryMetadataResponse>))]
[JsonSerializable(typeof(DiscoveryMetadataResponse))]
[JsonSerializable(typeof(DiscoveryMetadataUpdateRequest))]
[JsonSerializable(typeof(DiscoveryContactPoint))]
[JsonSerializable(typeof(DiscoveryLink))]
internal sealed partial class ServiceSettingsJsonContext : JsonSerializerContext
{
}
