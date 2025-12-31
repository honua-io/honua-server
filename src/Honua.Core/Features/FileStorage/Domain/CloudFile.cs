// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.FileStorage.Domain;

/// <summary>
/// Represents a file stored in cloud storage
/// </summary>
public sealed record CloudFile
{
    /// <summary>
    /// Unique identifier for the file in the storage system
    /// </summary>
    public required string FileId { get; init; }

    /// <summary>
    /// Original filename as provided during upload
    /// </summary>
    public required string FileName { get; init; }

    /// <summary>
    /// Storage path or key within the bucket/container
    /// </summary>
    public required string StoragePath { get; init; }

    /// <summary>
    /// MIME content type of the file
    /// </summary>
    public required string ContentType { get; init; }

    /// <summary>
    /// Size of the file in bytes
    /// </summary>
    public required long SizeBytes { get; init; }

    /// <summary>
    /// Timestamp when the file was uploaded
    /// </summary>
    public required DateTimeOffset UploadedAt { get; init; }

    /// <summary>
    /// Timestamp when the file expires and should be cleaned up (null for permanent storage)
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// MD5 hash of the file content for integrity verification
    /// </summary>
    public string? ContentHash { get; init; }

    /// <summary>
    /// Custom metadata associated with the file
    /// </summary>
    public ImmutableDictionary<string, string> Metadata { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>
    /// The cloud storage provider where this file is stored
    /// </summary>
    public required CloudStorageProvider Provider { get; init; }
}
