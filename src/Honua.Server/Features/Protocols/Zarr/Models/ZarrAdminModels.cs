// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Server.Features.Protocols.Zarr.Models;

/// <summary>
/// Request to register a Zarr store for read-only serving.
/// </summary>
internal sealed record RegisterZarrRequest
{
    /// <summary>
    /// Layer to associate the Zarr store with.
    /// </summary>
    public int LayerId { get; init; }

    /// <summary>
    /// Human-readable name.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Optional description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Cloud storage provider.
    /// </summary>
    public CloudStorageProvider Provider { get; init; }

    /// <summary>
    /// Bucket or container name.
    /// </summary>
    public string Bucket { get; init; } = string.Empty;

    /// <summary>
    /// Root prefix within the bucket pointing at the Zarr store root.
    /// </summary>
    public string RootPath { get; init; } = string.Empty;

    /// <summary>
    /// Validates the request fields.
    /// </summary>
    public bool IsValid(out string error)
    {
        if (LayerId <= 0)
        {
            error = "LayerId must be a positive integer.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(Name))
        {
            error = "Name is required.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(Bucket))
        {
            error = "Bucket is required.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(RootPath))
        {
            error = "RootPath is required.";
            return false;
        }
        if (RootPath.Contains("..", StringComparison.Ordinal) ||
            RootPath.Contains('\\', StringComparison.Ordinal) ||
            RootPath.StartsWith('/'))
        {
            error = "RootPath must be a relative object key with no traversal sequences.";
            return false;
        }
        if (Bucket.Length > 255 || RootPath.Length > 1024)
        {
            error = "Bucket must be 255 characters or fewer and RootPath must be 1024 characters or fewer.";
            return false;
        }
        if (!Enum.IsDefined(Provider))
        {
            error = "Provider must be one of: AwsS3, AzureBlob, Local.";
            return false;
        }
        error = string.Empty;
        return true;
    }
}

/// <summary>
/// Summary projection of a registered Zarr store.
/// </summary>
internal sealed record ZarrRegistrationResponse
{
    /// <summary>Registration identifier.</summary>
    public long Id { get; init; }

    /// <summary>Associated layer.</summary>
    public int LayerId { get; init; }

    /// <summary>Registration name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Optional description.</summary>
    public string? Description { get; init; }

    /// <summary>Cloud storage provider name.</summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>Bucket or container name.</summary>
    public string Bucket { get; init; } = string.Empty;

    /// <summary>Root key/prefix pointing at the Zarr store root.</summary>
    public string RootPath { get; init; } = string.Empty;

    /// <summary>Detected Zarr format version (2 or 3).</summary>
    public int? ZarrFormat { get; init; }

    /// <summary>Spatial reference identifier discovered during the metadata scan.</summary>
    public int? Srid { get; init; }

    /// <summary>Number of arrays/variables discovered in the store.</summary>
    public int? VariableCount { get; init; }

    /// <summary>Primary variable name when declared in the manifest.</summary>
    public string? PrimaryVariable { get; init; }

    /// <summary>Per-variable summaries.</summary>
    public ZarrVariableSummary[]? Variables { get; init; }

    /// <summary>When metadata was last scanned.</summary>
    public DateTimeOffset? MetadataScannedAt { get; init; }

    /// <summary>Registration timestamp.</summary>
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Lightweight description of a single Zarr variable for admin responses.
/// </summary>
internal sealed record ZarrVariableSummary
{
    /// <summary>Variable name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Per-dimension shape.</summary>
    public int[] Shape { get; init; } = [];

    /// <summary>Per-dimension chunk shape.</summary>
    public int[] Chunks { get; init; } = [];

    /// <summary>numpy-style dtype.</summary>
    public string DataType { get; init; } = string.Empty;

    /// <summary>Compressor codec id (e.g. <c>zlib</c>), or null when uncompressed.</summary>
    public string? Compressor { get; init; }

    /// <summary>Dimension names from <c>_ARRAY_DIMENSIONS</c> when present.</summary>
    public string[] DimensionNames { get; init; } = [];
}
