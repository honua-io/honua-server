// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// JSON source generation context for the layer/service metadata authoring admin APIs
/// (display hints, editor tracking, discovery metadata, CRS / spatial authoring).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(LayerDisplayUpdateRequest))]
[JsonSerializable(typeof(LayerDisplayResponse))]
[JsonSerializable(typeof(LayerEditingUpdateRequest))]
[JsonSerializable(typeof(LayerEditingResponse))]
[JsonSerializable(typeof(DiscoveryMetadataUpdateRequest))]
[JsonSerializable(typeof(DiscoveryMetadataResponse))]
[JsonSerializable(typeof(DiscoveryContactPoint))]
[JsonSerializable(typeof(DiscoveryLink))]
[JsonSerializable(typeof(LayerSpatialUpdateRequest))]
[JsonSerializable(typeof(LayerSpatialResponse))]
[JsonSerializable(typeof(SpatialReferencePayload))]
[JsonSerializable(typeof(ApiResponse<LayerDisplayResponse>))]
[JsonSerializable(typeof(ApiResponse<LayerEditingResponse>))]
[JsonSerializable(typeof(ApiResponse<DiscoveryMetadataResponse>))]
[JsonSerializable(typeof(ApiResponse<LayerSpatialResponse>))]
[JsonSerializable(typeof(ApiResponse<object>))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class LayerMetadataAuthoringJsonContext : JsonSerializerContext
{
}
