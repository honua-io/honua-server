// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Tiles.PMTiles;

/// <summary>
/// Strategy for how clients access a published PMTiles artifact.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PMTilesUrlStrategy>))]
public enum PMTilesUrlStrategy
{
    /// <summary>
    /// Object is reachable via a stable public base URL (no signature, no expiry).
    /// </summary>
    PublicUrl = 0,

    /// <summary>
    /// Browser fetches the object via a presigned/SAS URL with a finite lifetime.
    /// </summary>
    SignedUrl = 1,

    /// <summary>
    /// Browser fetches range-request bytes through a Honua-hosted proxy endpoint.
    /// </summary>
    RangeProxy = 2
}

/// <summary>
/// Provider-agnostic descriptor for a durable PMTiles artifact published from a
/// tile-operations publish job. Carries enough fields for MapLibre/PMTiles
/// clients to construct a <c>pmtiles://</c> source without ad hoc configuration.
/// </summary>
public sealed record PMTilesArtifactDescriptor
{
    /// <summary>
    /// Stable identifier for the artifact. Maps to <see cref="CloudFile.FileId"/>
    /// and is also used as the <c>artifactId</c> path segment when
    /// <see cref="UrlStrategy"/> is <see cref="PMTilesUrlStrategy.RangeProxy"/>.
    /// </summary>
    public required string ArtifactId { get; init; }

    /// <summary>
    /// Cloud storage provider hosting the underlying object.
    /// </summary>
    public required CloudStorageProvider StorageProvider { get; init; }

    /// <summary>
    /// S3 bucket name or Azure blob container name.
    /// </summary>
    public required string Bucket { get; init; }

    /// <summary>
    /// Full provider-agnostic object key / blob path within the bucket/container.
    /// </summary>
    public required string ObjectKey { get; init; }

    /// <summary>
    /// MIME type of the artifact. Always <c>application/vnd.pmtiles</c> for v1.
    /// </summary>
    public required string ContentType { get; init; }

    /// <summary>
    /// Size of the published artifact in bytes.
    /// </summary>
    public required long SizeBytes { get; init; }

    /// <summary>
    /// URL strategy used to construct <see cref="AccessUrl"/>.
    /// </summary>
    public required PMTilesUrlStrategy UrlStrategy { get; init; }

    /// <summary>
    /// Browser-usable access URL. Absolute public URL for
    /// <see cref="PMTilesUrlStrategy.PublicUrl"/>, absolute presigned/SAS URL for
    /// <see cref="PMTilesUrlStrategy.SignedUrl"/>, or root-relative proxy path
    /// (<c>/api/v1/tiles/pmtiles/{artifactId}</c>) for
    /// <see cref="PMTilesUrlStrategy.RangeProxy"/>. Browser clients should
    /// resolve the relative form against the Honua server origin.
    /// </summary>
    public required string AccessUrl { get; init; }

    /// <summary>
    /// When <see cref="AccessUrl"/> stops being valid. Null for
    /// <see cref="PMTilesUrlStrategy.PublicUrl"/> and
    /// <see cref="PMTilesUrlStrategy.RangeProxy"/>.
    /// </summary>
    public DateTimeOffset? AccessUrlExpiresAt { get; init; }

    /// <summary>
    /// When the artifact was published.
    /// </summary>
    public required DateTimeOffset PublishedAt { get; init; }

    /// <summary>
    /// Minimum zoom present in the archive (MapLibre source hint).
    /// </summary>
    public required int MinZoom { get; init; }

    /// <summary>
    /// Maximum zoom present in the archive (MapLibre source hint).
    /// </summary>
    public required int MaxZoom { get; init; }

    /// <summary>
    /// WGS84 bounding box as [west, south, east, north] (MapLibre source hint).
    /// </summary>
    public double[]? Bounds { get; init; }

    /// <summary>
    /// Layer identifier the artifact was generated for.
    /// </summary>
    public int? LayerId { get; init; }

    /// <summary>
    /// Service identifier the artifact was generated for, if any.
    /// </summary>
    public string? ServiceId { get; init; }

    /// <summary>
    /// Tile matrix set the artifact was generated for. Defaults to
    /// <c>WebMercatorQuad</c> for current implementations.
    /// </summary>
    public string? TileMatrixSetId { get; init; }
}
