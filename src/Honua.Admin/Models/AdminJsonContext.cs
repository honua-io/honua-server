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
[JsonSerializable(typeof(ApiResponse<AdminVersionResponse>), TypeInfoPropertyName = "ApiResponseAdminVersionResponse")]
[JsonSerializable(typeof(ApiResponse<AdminCapabilitiesResponse>), TypeInfoPropertyName = "ApiResponseAdminCapabilitiesResponse")]
[JsonSerializable(typeof(ApiResponse<MetadataManifest>), TypeInfoPropertyName = "ApiResponseMetadataManifest")]
[JsonSerializable(typeof(ApiResponse<MetadataResource[]>), TypeInfoPropertyName = "ApiResponseMetadataResourceArray")]
[JsonSerializable(typeof(ApiResponse<MetadataResource>), TypeInfoPropertyName = "ApiResponseMetadataResource")]
[JsonSerializable(typeof(ApiResponse<ManifestApplyResult>), TypeInfoPropertyName = "ApiResponseManifestApplyResult")]
[JsonSerializable(typeof(ApiResponse<object>), TypeInfoPropertyName = "ApiResponseObject")]
[JsonSerializable(typeof(AdminVersionResponse))]
[JsonSerializable(typeof(AdminCapabilitiesResponse))]
[JsonSerializable(typeof(MetadataManifest))]
[JsonSerializable(typeof(ManifestApplyRequest))]
[JsonSerializable(typeof(ManifestApplyResult))]
[JsonSerializable(typeof(ManifestApplySummary))]
[JsonSerializable(typeof(ManifestApplyEntry))]
[JsonSerializable(typeof(MetadataResource))]
[JsonSerializable(typeof(MetadataResource[]))]
[JsonSerializable(typeof(MetadataResourceWithEtag))]
[JsonSerializable(typeof(ResourceMetadata))]
[JsonSerializable(typeof(MetadataResourceIdentifier))]
internal sealed partial class AdminJsonContext : JsonSerializerContext
{
}
