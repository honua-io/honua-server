// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.Infrastructure.Domain;

public sealed record CloudFile
{
    public required string FileId { get; init; }

    public required string FileName { get; init; }

    public required string StoragePath { get; init; }

    public required string ContentType { get; init; }

    public required long SizeBytes { get; init; }

    public required DateTimeOffset UploadedAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public string? ContentHash { get; init; }

    public string? ETag { get; init; }

    public ImmutableDictionary<string, string> Metadata { get; init; } = ImmutableDictionary<string, string>.Empty;

    public required CloudStorageProvider Provider { get; init; }
}
