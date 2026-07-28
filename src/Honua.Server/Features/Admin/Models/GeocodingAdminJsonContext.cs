// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// JSON source generation context for geocoding admin API models.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ApiResponse<GeocodingProvidersResponse>))]
[JsonSerializable(typeof(ApiResponse<object>))]
[JsonSerializable(typeof(GeocodingProvidersResponse))]
[JsonSerializable(typeof(GeocodingProviderDetail))]
[JsonSerializable(typeof(GeocodingProviderDetail[]))]
[JsonSerializable(typeof(GeocodingProviderCapabilitiesDto))]
[JsonSerializable(typeof(ApiResponse<GeocoderReferenceDataImportResponse>))]
[JsonSerializable(typeof(GeocoderReferenceDataImportResponse))]
[JsonSerializable(typeof(GeocoderReferenceReportEntryDto[]))]
[JsonSerializable(typeof(GeocoderReferenceSkippedRowDto[]))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class GeocodingAdminJsonContext : JsonSerializerContext
{
}
