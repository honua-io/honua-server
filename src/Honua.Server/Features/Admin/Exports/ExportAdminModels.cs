// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Exports;

public sealed record ExportSourceRequest
{
    // "layer"; "upload" and "object" sources arrive with object-storage Connections.
    public string? Kind { get; init; }

    public string? ServiceId { get; init; }

    public int? LayerId { get; init; }

    public string? Name { get; init; }
}

public sealed record ExportCreateRequest
{
    public ExportSourceRequest? Source { get; init; }

    // Defaults to the Layer's name as a slug.
    public string? ExportId { get; init; }

    // Defaults to the Layer's declared text key, or the object id as text.
    public string? IdentityField { get; init; }

    public int? MinZoom { get; init; }

    public int? MaxZoom { get; init; }

    public int? MaxTiles { get; init; }
}

public sealed record ExportListResponse(IReadOnlyList<ExportResponse> Items);

public sealed record ExportResponse
{
    public const string Running = "running";
    public const string Ready = "ready";
    public const string Failed = "failed";
    public const string Unpublished = "unpublished";

    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string Status { get; init; }

    public required ExportSourceRequest Source { get; init; }

    public string? CurrentVersion { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    public string? CollectionUrl { get; init; }

    // The public data path arrives with public promotion; until then an Export is signed-in only.
    public string? PublicUrl { get; init; }

    public string? NotPublicReason { get; init; }

    public string? JobId { get; init; }

    public string? Message { get; init; }

    public IReadOnlyList<ExportVersionResponse> Versions { get; init; } = [];
}

public sealed record ExportVersionResponse
{
    public required string Version { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public long? FeatureCount { get; init; }

    public required IReadOnlyList<ExportArtifactResponse> Artifacts { get; init; }
}

public sealed record ExportArtifactResponse(string Kind, string Href, long? Bytes);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ExportCreateRequest))]
[JsonSerializable(typeof(ApiResponse<ExportResponse>))]
[JsonSerializable(typeof(ApiResponse<ExportListResponse>))]
[JsonSerializable(typeof(ApiResponse<object>))]
internal sealed partial class ExportAdminJsonContext : JsonSerializerContext;
