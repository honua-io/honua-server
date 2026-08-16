// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Studio.Drafts;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Studio.Models;

/// <summary>
/// Response payload for the deterministic Studio map-package draft endpoint
/// (ADR-0076).
/// </summary>
public sealed record StudioMapPackageDraftResponse
{
    /// <summary>Stable <c>map_…</c> identifier of the created draft.</summary>
    [JsonPropertyName("packageId")]
    public required string PackageId { get; init; }

    /// <summary>The created draft package.</summary>
    [JsonPropertyName("package")]
    public required MapPackage Package { get; init; }

    /// <summary>
    /// Non-blocking findings deferred to publish-time resolution (for example a
    /// source binding whose locator is not yet known).
    /// </summary>
    [JsonPropertyName("warnings")]
    public IReadOnlyList<PackageDraftFinding> Warnings { get; init; } = [];
}

/// <summary>
/// Response payload for the deterministic Studio app-package draft endpoint
/// (ADR-0076).
/// </summary>
public sealed record StudioAppPackageDraftResponse
{
    /// <summary>Stable <c>app_…</c> identifier of the created draft.</summary>
    [JsonPropertyName("packageId")]
    public required string PackageId { get; init; }

    /// <summary>The created draft package.</summary>
    [JsonPropertyName("package")]
    public required AppPackage Package { get; init; }

    /// <summary>
    /// Non-blocking findings deferred to publish-time resolution.
    /// </summary>
    [JsonPropertyName("warnings")]
    public IReadOnlyList<PackageDraftFinding> Warnings { get; init; } = [];
}

/// <summary>
/// Source-generated JSON context for the deterministic Studio package-draft
/// endpoints.
/// </summary>
/// <remarks>
/// The request bodies are the canonical <see cref="MapPackageDraftRequest"/> /
/// <see cref="AppPackageDraftRequest"/> themselves rather than adapter-local
/// mirrors: ADR-0076 makes those types deliberately permissive wire shapes so
/// every validation rule lives in the shared factory instead of being restated
/// by each protocol adapter. <c>UseStringEnumConverter</c> keeps the nested
/// package enums (<c>PackageStatus</c>, <c>SourceProtocol</c>) round-tripping by
/// name, matching <see cref="PackagingJsonContext"/>.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(MapPackageDraftRequest))]
[JsonSerializable(typeof(AppPackageDraftRequest))]
[JsonSerializable(typeof(ApiResponse<StudioMapPackageDraftResponse>))]
[JsonSerializable(typeof(ApiResponse<StudioAppPackageDraftResponse>))]
internal sealed partial class StudioPackageDraftJsonContext : JsonSerializerContext;
