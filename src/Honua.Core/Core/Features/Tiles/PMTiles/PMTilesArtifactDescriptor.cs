// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Tiles.PMTiles;

[JsonConverter(typeof(JsonStringEnumConverter<PMTilesUrlStrategy>))]
public enum PMTilesUrlStrategy
{
    PublicUrl = 0,

    SignedUrl = 1,

    RangeProxy = 2
}

public sealed record PMTilesArtifactDescriptor
{
    public required string ArtifactId { get; init; }

    public required CloudStorageProvider StorageProvider { get; init; }

    public required string Bucket { get; init; }

    public required string ObjectKey { get; init; }

    public required string ContentType { get; init; }

    public required long SizeBytes { get; init; }

    public required PMTilesUrlStrategy UrlStrategy { get; init; }

    public required string AccessUrl { get; init; }

    public DateTimeOffset? AccessUrlExpiresAt { get; init; }

    public required DateTimeOffset PublishedAt { get; init; }

    public required int MinZoom { get; init; }

    public required int MaxZoom { get; init; }

    public double[]? Bounds { get; init; }

    public int? LayerId { get; init; }

    public string? ServiceId { get; init; }

    public string? TileMatrixSetId { get; init; }
}
