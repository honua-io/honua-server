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
[JsonSerializable(typeof(HealthMetrics))]
[JsonSerializable(typeof(PerformanceMetricsResponse))]
[JsonSerializable(typeof(SystemInfo))]
[JsonSerializable(typeof(HttpRequestMetrics))]
[JsonSerializable(typeof(DatabaseMetrics))]
[JsonSerializable(typeof(DatabaseOperationMetrics))]
[JsonSerializable(typeof(CacheMetrics))]
[JsonSerializable(typeof(CacheTypeMetrics))]
[JsonSerializable(typeof(MemoryUsage))]
[JsonSerializable(typeof(RecentErrorEntry))]
[JsonSerializable(typeof(RecentErrorsResponse))]
[JsonSerializable(typeof(ObservabilityStatusResponse))]
[JsonSerializable(typeof(ApiResponse<ServiceSettingsResponse>), TypeInfoPropertyName = "ApiResponseServiceSettingsResponse")]
[JsonSerializable(typeof(ApiResponse<ServiceSummary[]>), TypeInfoPropertyName = "ApiResponseServiceSummaryArray")]
[JsonSerializable(typeof(ServiceSettingsResponse))]
[JsonSerializable(typeof(MapServerSettingsResponse))]
[JsonSerializable(typeof(UpdateProtocolsRequest))]
[JsonSerializable(typeof(UpdateMapServerSettingsRequest))]
[JsonSerializable(typeof(ServiceSummary))]
[JsonSerializable(typeof(ServiceSummary[]))]
internal sealed partial class AdminJsonContext : JsonSerializerContext
{
}
