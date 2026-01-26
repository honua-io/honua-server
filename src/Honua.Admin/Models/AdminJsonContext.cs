// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Admin.Models;

/// <summary>
/// JSON source generation context for Honua.Admin API models.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ApiResponse<LayerStyleResponse>), TypeInfoPropertyName = "ApiResponseLayerStyleResponse")]
[JsonSerializable(typeof(LayerStyleUpdateRequest))]
[JsonSerializable(typeof(LayerStyleResponse))]
[JsonSerializable(typeof(TileJsonResponse))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class AdminJsonContext : JsonSerializerContext
{
}
