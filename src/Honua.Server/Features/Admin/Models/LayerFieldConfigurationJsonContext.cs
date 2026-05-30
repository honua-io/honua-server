// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// JSON source generation context for layer field configuration admin APIs.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(LayerFieldConfigurationUpdateRequest))]
[JsonSerializable(typeof(LayerFieldConfigurationUpdateItem))]
[JsonSerializable(typeof(LayerFieldConfigurationResponse))]
[JsonSerializable(typeof(LayerFieldConfigurationItem))]
[JsonSerializable(typeof(LayerFilterConfigurationUpdateRequest))]
[JsonSerializable(typeof(LayerFilterConfigurationResponse))]
[JsonSerializable(typeof(LayerPermanentFilterConfiguration))]
[JsonSerializable(typeof(ApiResponse<LayerFieldConfigurationResponse>))]
[JsonSerializable(typeof(ApiResponse<LayerFilterConfigurationResponse>))]
[JsonSerializable(typeof(ApiResponse<object>))]
[JsonSerializable(typeof(MetadataV2FieldDomain))]
[JsonSerializable(typeof(MetadataV2CodedValue))]
[JsonSerializable(typeof(MetadataV2CodedValue[]))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class LayerFieldConfigurationJsonContext : JsonSerializerContext
{
}
