// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Server.Features.Protocols.Cog.Models;

/// <summary>
/// Request to register a cloud-hosted COG for direct serving.
/// </summary>
internal sealed record RegisterCogRequest
{
    /// <summary>
    /// Layer to associate the COG with.
    /// </summary>
    public int LayerId { get; init; }

    /// <summary>
    /// Human-readable name for the registration.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Optional description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Cloud storage provider hosting the COG.
    /// </summary>
    public CloudStorageProvider Provider { get; init; }

    /// <summary>
    /// Bucket or container name.
    /// </summary>
    public string Bucket { get; init; } = string.Empty;

    /// <summary>
    /// Object key or blob path.
    /// </summary>
    public string ObjectKey { get; init; } = string.Empty;

    /// <summary>
    /// Validates the request fields.
    /// </summary>
    public bool IsValid(out string error)
    {
        if (LayerId <= 0) { error = "LayerId must be a positive integer."; return false; }
        if (string.IsNullOrWhiteSpace(Name)) { error = "Name is required."; return false; }
        if (string.IsNullOrWhiteSpace(Bucket)) { error = "Bucket is required."; return false; }
        if (string.IsNullOrWhiteSpace(ObjectKey)) { error = "ObjectKey is required."; return false; }
        if (!Enum.IsDefined(Provider)) { error = "Provider must be one of: AwsS3, AzureBlob."; return false; }
        if (Provider == CloudStorageProvider.Local) { error = "Local storage is not supported for COG serving."; return false; }
        error = string.Empty;
        return true;
    }
}

/// <summary>
/// Response for a COG registration.
/// </summary>
internal sealed record CogRegistrationResponse
{
    /// <summary>
    /// Registration identifier.
    /// </summary>
    public long Id { get; init; }

    /// <summary>
    /// Associated layer.
    /// </summary>
    public int LayerId { get; init; }

    /// <summary>
    /// Registration name.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Optional description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Cloud storage provider.
    /// </summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>
    /// Bucket or container name.
    /// </summary>
    public string Bucket { get; init; } = string.Empty;

    /// <summary>
    /// Object key or blob path.
    /// </summary>
    public string ObjectKey { get; init; } = string.Empty;

    /// <summary>
    /// Image width (null if metadata not yet scanned).
    /// </summary>
    public int? Width { get; init; }

    /// <summary>
    /// Image height (null if metadata not yet scanned).
    /// </summary>
    public int? Height { get; init; }

    /// <summary>
    /// Number of bands.
    /// </summary>
    public int? BandCount { get; init; }

    /// <summary>
    /// Spatial reference system identifier.
    /// </summary>
    public int? Srid { get; init; }

    /// <summary>
    /// Compression type.
    /// </summary>
    public string? Compression { get; init; }

    /// <summary>
    /// Number of overview levels.
    /// </summary>
    public int? OverviewLevelCount { get; init; }

    /// <summary>
    /// When metadata was last scanned.
    /// </summary>
    public DateTimeOffset? MetadataScannedAt { get; init; }

    /// <summary>
    /// Registration timestamp.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }
}
