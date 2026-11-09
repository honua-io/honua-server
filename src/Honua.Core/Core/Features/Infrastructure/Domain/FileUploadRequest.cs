// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.Infrastructure.Domain;

public sealed record FileUploadRequest
{
    public required Stream Content { get; init; }

    public required string FileName { get; init; }

    public required string ContentType { get; init; }

    public long? SizeBytes { get; init; }

    public TimeSpan? TimeToLive { get; init; }

    public ImmutableDictionary<string, string> Metadata { get; init; } = ImmutableDictionary<string, string>.Empty;

    public string? Folder { get; init; }

    public IProgress<UploadProgress>? Progress { get; init; }

    public string? ObjectKeyOverride { get; init; }

    public int ChunkSize { get; init; } = 5 * 1024 * 1024; // 5MB

    public bool EnableChunkedUpload { get; init; } = true;

    public string UploadId { get; init; } = Guid.NewGuid().ToString();
}

public sealed record ByteArrayUploadRequest
{
    public required byte[] Content { get; init; }

    public required string FileName { get; init; }

    public required string ContentType { get; init; }

    public TimeSpan? TimeToLive { get; init; }

    public ImmutableDictionary<string, string> Metadata { get; init; } = ImmutableDictionary<string, string>.Empty;

    public string? Folder { get; init; }
}

public sealed record BatchFileItem
{
    public required Stream Content { get; init; }

    public required string FileName { get; init; }

    public required string ContentType { get; init; }

    public long? SizeBytes { get; init; }

    public ImmutableDictionary<string, string> Metadata { get; init; } = ImmutableDictionary<string, string>.Empty;
}

public sealed record BatchUploadRequest
{
    public required IReadOnlyList<BatchFileItem> Files { get; init; }

    public TimeSpan? TimeToLive { get; init; }

    public string? Folder { get; init; }

    public bool ContinueOnError { get; init; }
}
