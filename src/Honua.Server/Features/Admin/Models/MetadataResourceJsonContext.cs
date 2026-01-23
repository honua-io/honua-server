// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Metadata.Domain;
using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// JSON source generation context for metadata resource admin APIs.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(MetadataResource))]
[JsonSerializable(typeof(MetadataResource[]))]
[JsonSerializable(typeof(MetadataResourceIdentifier))]
[JsonSerializable(typeof(MetadataManifest))]
[JsonSerializable(typeof(ManifestApplyRequest))]
[JsonSerializable(typeof(ManifestApplyResult))]
[JsonSerializable(typeof(ManifestApplyEntry))]
[JsonSerializable(typeof(ManifestApplySummary))]
[JsonSerializable(typeof(AdminVersionResponse))]
[JsonSerializable(typeof(AdminCapabilitiesResponse))]
[JsonSerializable(typeof(MetadataCompilationStatus))]
[JsonSerializable(typeof(ApiResponse<MetadataResource>))]
[JsonSerializable(typeof(ApiResponse<MetadataResource[]>))]
[JsonSerializable(typeof(ApiResponse<MetadataManifest>))]
[JsonSerializable(typeof(ApiResponse<ManifestApplyResult>))]
[JsonSerializable(typeof(ApiResponse<AdminVersionResponse>))]
[JsonSerializable(typeof(ApiResponse<AdminCapabilitiesResponse>))]
[JsonSerializable(typeof(ApiResponse<object>))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class MetadataResourceJsonContext : JsonSerializerContext
{
}
