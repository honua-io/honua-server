// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Attachments.Domain;

/// <summary>
/// Represents a file attachment associated with a geographic feature
/// </summary>
public readonly record struct Attachment
{
    /// <summary>
    /// Unique identifier for the attachment
    /// </summary>
    public required long Id { get; init; }

    /// <summary>
    /// Identifier of the feature this attachment belongs to
    /// </summary>
    public required long FeatureId { get; init; }

    /// <summary>
    /// Identifier of the layer containing the feature
    /// </summary>
    public required int LayerId { get; init; }

    /// <summary>
    /// Original filename of the attachment
    /// </summary>
    public required string Filename { get; init; }

    /// <summary>
    /// MIME content type of the file
    /// </summary>
    public required string ContentType { get; init; }

    /// <summary>
    /// Size of the file in bytes
    /// </summary>
    public required long Size { get; init; }

    /// <summary>
    /// Date and time when the attachment was uploaded
    /// </summary>
    public required DateTime CreatedAt { get; init; }

    /// <summary>
    /// Storage key or file identifier within the configured storage provider
    /// </summary>
    public required string StoragePath { get; init; }

    /// <summary>
    /// Optional keywords or tags for the attachment
    /// </summary>
    public string? Keywords { get; init; }

    /// <summary>
    /// Creates a new attachment with the specified properties
    /// </summary>
    /// <param name="id">Attachment identifier</param>
    /// <param name="featureId">Feature identifier</param>
    /// <param name="layerId">Layer identifier</param>
    /// <param name="filename">Original filename</param>
    /// <param name="contentType">MIME content type</param>
    /// <param name="size">File size in bytes</param>
    /// <param name="createdAt">Upload timestamp</param>
    /// <param name="storagePath">Storage key or file identifier</param>
    /// <param name="keywords">Optional keywords</param>
    /// <returns>New attachment instance</returns>
    public static Attachment Create(
        long id,
        long featureId,
        int layerId,
        string filename,
        string contentType,
        long size,
        DateTime createdAt,
        string storagePath,
        string? keywords = null)
        => new()
        {
            Id = id,
            FeatureId = featureId,
            LayerId = layerId,
            Filename = filename,
            ContentType = contentType,
            Size = size,
            CreatedAt = createdAt,
            StoragePath = storagePath,
            Keywords = keywords
        };

    /// <summary>
    /// Creates a new attachment for upload with auto-generated timestamp
    /// </summary>
    /// <param name="id">Attachment identifier</param>
    /// <param name="featureId">Feature identifier</param>
    /// <param name="layerId">Layer identifier</param>
    /// <param name="filename">Original filename</param>
    /// <param name="contentType">MIME content type</param>
    /// <param name="size">File size in bytes</param>
    /// <param name="storagePath">Storage key or file identifier</param>
    /// <param name="keywords">Optional keywords</param>
    /// <returns>New attachment instance with current timestamp</returns>
    public static Attachment CreateForUpload(
        long id,
        long featureId,
        int layerId,
        string filename,
        string contentType,
        long size,
        string storagePath,
        string? keywords = null)
        => Create(id, featureId, layerId, filename, contentType, size, DateTime.UtcNow, storagePath, keywords);
}
