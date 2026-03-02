// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Sdk.Admin.Models;

namespace Honua.Sdk.Admin;

/// <summary>
/// Source-generated JSON serializer context for AOT-compatible serialization.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ApiResponse<ServiceSummary[]>))]
[JsonSerializable(typeof(ApiResponse<ServiceSettingsResponse>))]
[JsonSerializable(typeof(ApiResponse<LayerMetadataResponse>))]
[JsonSerializable(typeof(ApiResponse<SecureConnectionSummary[]>))]
[JsonSerializable(typeof(ApiResponse<SecureConnectionDetail>))]
[JsonSerializable(typeof(ApiResponse<SecureConnectionSummary>))]
[JsonSerializable(typeof(ApiResponse<ConnectionTestResult>))]
[JsonSerializable(typeof(ApiResponse<EncryptionValidationResult>))]
[JsonSerializable(typeof(ApiResponse<KeyRotationResult>))]
[JsonSerializable(typeof(ApiResponse<MetadataResource[]>))]
[JsonSerializable(typeof(ApiResponse<MetadataResource>))]
[JsonSerializable(typeof(ApiResponse<MetadataManifest>))]
[JsonSerializable(typeof(ApiResponse<ManifestApplyResult>))]
[JsonSerializable(typeof(ApiResponse<AdminVersionResponse>))]
[JsonSerializable(typeof(ApiResponse<AdminCapabilitiesResponse>))]
[JsonSerializable(typeof(ApiResponse<PublishedLayerSummary[]>))]
[JsonSerializable(typeof(ApiResponse<PublishedLayerSummary>))]
[JsonSerializable(typeof(ApiResponse<LayerStyleResponse>))]
[JsonSerializable(typeof(ApiResponse<object>))]
[JsonSerializable(typeof(TableDiscoveryResponse))]
[JsonSerializable(typeof(UpdateProtocolsRequest))]
[JsonSerializable(typeof(UpdateMapServerSettingsRequest))]
[JsonSerializable(typeof(UpdateAccessPolicyRequest))]
[JsonSerializable(typeof(UpdateTimeInfoRequest))]
[JsonSerializable(typeof(UpdateLayerMetadataRequest))]
[JsonSerializable(typeof(CreateSecureConnectionRequest))]
[JsonSerializable(typeof(UpdateSecureConnectionRequest))]
[JsonSerializable(typeof(MetadataResource))]
[JsonSerializable(typeof(ManifestApplyRequest))]
[JsonSerializable(typeof(PublishLayerRequest))]
[JsonSerializable(typeof(LayerEnabledRequest))]
[JsonSerializable(typeof(LayerStyleUpdateRequest))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class HonuaAdminJsonContext : JsonSerializerContext
{
}
