// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// JSON source generation context for geoprocessing usage-ranking admin API models.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ApiResponse<GeoprocessingUsageRankingResponse>))]
[JsonSerializable(typeof(ApiResponse<object>))]
[JsonSerializable(typeof(GeoprocessingUsageRankingResponse))]
[JsonSerializable(typeof(GeoprocessingToolUsageRankEntry))]
[JsonSerializable(typeof(GeoprocessingToolUsageRankEntry[]))]
internal sealed partial class GeoprocessingUsageJsonContext : JsonSerializerContext
{
}
