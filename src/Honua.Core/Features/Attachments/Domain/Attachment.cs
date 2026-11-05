// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Attachments.Domain;

public readonly record struct Attachment
{
    public required long Id { get; init; }

    public required long FeatureId { get; init; }

    public required int LayerId { get; init; }

    public required string Filename { get; init; }

    public required string ContentType { get; init; }

    public required long Size { get; init; }

    public required DateTime CreatedAt { get; init; }

    public required string StoragePath { get; init; }

    public string? Keywords { get; init; }

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
