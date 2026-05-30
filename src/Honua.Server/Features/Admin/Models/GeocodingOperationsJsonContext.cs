// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// JSON source generation context for geocoding operations admin API models.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ApiResponse<GeocodingOperationsProvidersResponse>))]
[JsonSerializable(typeof(ApiResponse<GeocodingConfigurationResponse>))]
[JsonSerializable(typeof(GeocodingOperationsProvidersResponse))]
[JsonSerializable(typeof(GeocodingOperationsProviderStatusResponse))]
[JsonSerializable(typeof(GeocodingOperationsProviderStatusResponse[]))]
[JsonSerializable(typeof(GeocodingOperationsCapabilitiesResponse))]
[JsonSerializable(typeof(GeocodingConfigurationResponse))]
internal sealed partial class GeocodingOperationsJsonContext : JsonSerializerContext
{
}
