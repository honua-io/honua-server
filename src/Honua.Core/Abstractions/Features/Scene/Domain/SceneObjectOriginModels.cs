// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Scene.Domain;

public readonly record struct SceneByteRange
{
    public SceneByteRange(long start, long length)
    {
        if (start < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(start), "Byte range start must be non-negative.");
        }

        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Byte range length must be positive.");
        }

        Start = start;
        Length = length;
    }

    public long Start { get; }

    public long Length { get; }

    public long End => Start + Length - 1;
}

public sealed record SceneObjectAssetMetadata
{
    public required string RelativePath { get; init; }

    public required string ContentType { get; init; }

    public required long ContentLength { get; init; }

    public required string ETag { get; init; }

    public required DateTimeOffset LastModifiedUtc { get; init; }

    public bool FromObjectStorage { get; init; }
}

public enum SceneObjectAssetReadStatus
{
    Found = 0,

    NotFound = 1,

    InvalidPath = 2,

    OutsideRoot = 3,

    ByteBudgetExceeded = 4,

    RangeUnsatisfiable = 5
}

public sealed record SceneObjectAssetReadResult : IAsyncDisposable, IDisposable
{
    public required SceneObjectAssetReadStatus Status { get; init; }

    public SceneObjectAssetMetadata? Metadata { get; init; }

    public Stream? Content { get; init; }

    public long? RangeStart { get; init; }

    public long? RangeEnd { get; init; }

    public string? ErrorDetail { get; init; }

    public static SceneObjectAssetReadResult Found(
        SceneObjectAssetMetadata metadata,
        Stream content,
        long? rangeStart = null,
        long? rangeEnd = null)
        => new()
        {
            Status = SceneObjectAssetReadStatus.Found,
            Metadata = metadata,
            Content = content,
            RangeStart = rangeStart,
            RangeEnd = rangeEnd
        };

    public static SceneObjectAssetReadResult NotFound()
        => new() { Status = SceneObjectAssetReadStatus.NotFound };

    public static SceneObjectAssetReadResult InvalidPath(string? detail = null)
        => new() { Status = SceneObjectAssetReadStatus.InvalidPath, ErrorDetail = detail };

    public static SceneObjectAssetReadResult OutsideRoot(string? detail = null)
        => new() { Status = SceneObjectAssetReadStatus.OutsideRoot, ErrorDetail = detail };

    public static SceneObjectAssetReadResult ByteBudgetExceeded(SceneObjectAssetMetadata? metadata, string detail)
        => new()
        {
            Status = SceneObjectAssetReadStatus.ByteBudgetExceeded,
            Metadata = metadata,
            ErrorDetail = detail
        };

    public static SceneObjectAssetReadResult RangeUnsatisfiable(SceneObjectAssetMetadata? metadata, string? detail = null)
        => new()
        {
            Status = SceneObjectAssetReadStatus.RangeUnsatisfiable,
            Metadata = metadata,
            ErrorDetail = detail
        };

    public ValueTask DisposeAsync()
        => Content is null ? ValueTask.CompletedTask : Content.DisposeAsync();

    public void Dispose()
        => Content?.Dispose();
}
