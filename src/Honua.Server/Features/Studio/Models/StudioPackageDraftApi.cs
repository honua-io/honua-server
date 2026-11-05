// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Studio.Drafts;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Studio.Models;

public sealed record StudioMapPackageDraftResponse
{
    [JsonPropertyName("packageId")]
    public required string PackageId { get; init; }

    [JsonPropertyName("package")]
    public required MapPackage Package { get; init; }

    [JsonPropertyName("warnings")]
    public IReadOnlyList<PackageDraftFinding> Warnings { get; init; } = [];
}

public sealed record StudioAppPackageDraftResponse
{
    [JsonPropertyName("packageId")]
    public required string PackageId { get; init; }

    [JsonPropertyName("package")]
    public required AppPackage Package { get; init; }

    [JsonPropertyName("warnings")]
    public IReadOnlyList<PackageDraftFinding> Warnings { get; init; } = [];
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(MapPackageDraftRequest))]
[JsonSerializable(typeof(AppPackageDraftRequest))]
[JsonSerializable(typeof(ApiResponse<StudioMapPackageDraftResponse>))]
[JsonSerializable(typeof(ApiResponse<StudioAppPackageDraftResponse>))]
internal sealed partial class StudioPackageDraftJsonContext : JsonSerializerContext;
