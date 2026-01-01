// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.FileStorage.Domain;

/// <summary>
/// Request to upload a file to cloud storage
/// </summary>
public sealed record FileUploadRequest
{
    /// <summary>
    /// Stream containing the file content
    /// </summary>
    public required Stream Content { get; init; }

    /// <summary>
    /// Original filename
    /// </summary>
    public required string FileName { get; init; }

    /// <summary>
    /// MIME content type of the file
    /// </summary>
    public required string ContentType { get; init; }

    /// <summary>
    /// Size of the file in bytes (optional, for validation)
    /// </summary>
    public long? SizeBytes { get; init; }

    /// <summary>
    /// Time-to-live for temporary files (null for permanent storage)
    /// </summary>
    public TimeSpan? TimeToLive { get; init; }

    /// <summary>
    /// Custom metadata to associate with the file
    /// </summary>
    public ImmutableDictionary<string, string> Metadata { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>
    /// Optional subfolder/prefix for organizing files
    /// </summary>
    public string? Folder { get; init; }
}

/// <summary>
/// Request to upload a file from a byte array
/// </summary>
public sealed record ByteArrayUploadRequest
{
    /// <summary>
    /// File content as byte array
    /// </summary>
    public required byte[] Content { get; init; }

    /// <summary>
    /// Original filename
    /// </summary>
    public required string FileName { get; init; }

    /// <summary>
    /// MIME content type of the file
    /// </summary>
    public required string ContentType { get; init; }

    /// <summary>
    /// Time-to-live for temporary files (null for permanent storage)
    /// </summary>
    public TimeSpan? TimeToLive { get; init; }

    /// <summary>
    /// Custom metadata to associate with the file
    /// </summary>
    public ImmutableDictionary<string, string> Metadata { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>
    /// Optional subfolder/prefix for organizing files
    /// </summary>
    public string? Folder { get; init; }
}

/// <summary>
/// Single file in a batch upload request
/// </summary>
public sealed record BatchFileItem
{
    /// <summary>
    /// Stream containing the file content
    /// </summary>
    public required Stream Content { get; init; }

    /// <summary>
    /// Original filename
    /// </summary>
    public required string FileName { get; init; }

    /// <summary>
    /// MIME content type of the file
    /// </summary>
    public required string ContentType { get; init; }

    /// <summary>
    /// Size of the file in bytes (optional, for validation)
    /// </summary>
    public long? SizeBytes { get; init; }

    /// <summary>
    /// Custom metadata to associate with the file
    /// </summary>
    public ImmutableDictionary<string, string> Metadata { get; init; } = ImmutableDictionary<string, string>.Empty;
}

/// <summary>
/// Request to upload multiple files as a batch (e.g., Shapefile components)
/// </summary>
public sealed record BatchUploadRequest
{
    /// <summary>
    /// Files to upload
    /// </summary>
    public required IReadOnlyList<BatchFileItem> Files { get; init; }

    /// <summary>
    /// Time-to-live for temporary files (null for permanent storage)
    /// </summary>
    public TimeSpan? TimeToLive { get; init; }

    /// <summary>
    /// Optional subfolder/prefix for organizing the batch
    /// </summary>
    public string? Folder { get; init; }

    /// <summary>
    /// Whether to continue uploading remaining files if one fails
    /// </summary>
    public bool ContinueOnError { get; init; }
}
