// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.Infrastructure.Domain;

public sealed record BatchUploadResult
{
    public required bool Success { get; init; }

    public required string BatchId { get; init; }

    public ImmutableList<CloudFile> UploadedFiles { get; init; } = ImmutableList<CloudFile>.Empty;

    public ImmutableDictionary<string, string> FailedFiles { get; init; } = ImmutableDictionary<string, string>.Empty;

    public int TotalFiles => UploadedFiles.Count + FailedFiles.Count;

    public int SuccessCount => UploadedFiles.Count;

    public int FailureCount => FailedFiles.Count;

    public TimeSpan Duration { get; init; }

    public static BatchUploadResult CreateSuccess(
        string batchId,
        IEnumerable<CloudFile> files,
        TimeSpan duration = default)
        => new()
        {
            Success = true,
            BatchId = batchId,
            UploadedFiles = files.ToImmutableList(),
            Duration = duration
        };

    public static BatchUploadResult CreatePartialSuccess(
        string batchId,
        IEnumerable<CloudFile> successfulFiles,
        IDictionary<string, string> failedFiles,
        TimeSpan duration = default)
        => new()
        {
            Success = false,
            BatchId = batchId,
            UploadedFiles = successfulFiles.ToImmutableList(),
            FailedFiles = failedFiles.ToImmutableDictionary(),
            Duration = duration
        };

    public static BatchUploadResult CreateFailure(
        string batchId,
        IDictionary<string, string> failedFiles,
        TimeSpan duration = default)
        => new()
        {
            Success = false,
            BatchId = batchId,
            FailedFiles = failedFiles.ToImmutableDictionary(),
            Duration = duration
        };
}
