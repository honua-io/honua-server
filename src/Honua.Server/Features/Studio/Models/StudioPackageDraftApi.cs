// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Studio.Drafts;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Studio.Models;

public sealed record StudioMapDraftResponse
{
    [JsonPropertyName("mapId")]
    public required string MapId { get; init; }

    [JsonPropertyName("map")]
    public required Map Map { get; init; }

    [JsonPropertyName("warnings")]
    public IReadOnlyList<PackageDraftFinding> Warnings { get; init; } = [];
}

public sealed record StudioAppDraftResponse
{
    [JsonPropertyName("appId")]
    public required string AppId { get; init; }

    [JsonPropertyName("app")]
    public required App App { get; init; }

    [JsonPropertyName("warnings")]
    public IReadOnlyList<PackageDraftFinding> Warnings { get; init; } = [];
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(MapDraftRequest))]
[JsonSerializable(typeof(AppDraftRequest))]
[JsonSerializable(typeof(ApiResponse<StudioMapDraftResponse>))]
[JsonSerializable(typeof(ApiResponse<StudioAppDraftResponse>))]
internal sealed partial class StudioPackageDraftJsonContext : JsonSerializerContext;
