// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Protocols.Rasters.CogArtifacts;

/// <summary>
/// Request to publish a layer's primary raster as a Cloud Optimized GeoTIFF artifact.
/// </summary>
internal sealed record CogArtifactPublishRequest
{
    /// <summary>Publication layer index whose primary raster is exported.</summary>
    public int LayerId { get; init; }
}

/// <summary>
/// A published Cloud Optimized GeoTIFF artifact, addressable by the public range proxy.
/// </summary>
internal sealed record CogArtifactDescriptor
{
    /// <summary>Artifact id, which is also the deterministic object key.</summary>
    public required string ArtifactId { get; init; }

    /// <summary>Publication layer index the artifact was exported from.</summary>
    public required int LayerId { get; init; }

    /// <summary>Primary raster id inside the layer.</summary>
    public required long RasterId { get; init; }

    /// <summary>Artifact size in bytes.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>Media type the proxy serves the artifact with.</summary>
    public required string ContentType { get; init; }

    /// <summary>Root-relative URL; authorized clients open it with HTTP range requests.</summary>
    public required string Url { get; init; }

    /// <summary>Exported raster width in pixels.</summary>
    public required int Width { get; init; }

    /// <summary>Exported raster height in pixels.</summary>
    public required int Height { get; init; }

    /// <summary>Band count of the exported raster.</summary>
    public required int BandCount { get; init; }

    /// <summary>Spatial reference of the exported raster, when known.</summary>
    public int? Srid { get; init; }

    /// <summary>When the artifact was written.</summary>
    public required DateTimeOffset PublishedAt { get; init; }
}

[JsonSourceGenerationOptions(System.Text.Json.JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(CogArtifactPublishRequest))]
[JsonSerializable(typeof(CogArtifactDescriptor))]
internal sealed partial class CogArtifactJsonContext : JsonSerializerContext
{
}
