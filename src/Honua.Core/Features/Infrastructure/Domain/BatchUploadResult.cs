// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.Infrastructure.Domain;

/// <summary>
/// Result of a batch file upload operation (e.g., Shapefile with multiple components)
/// </summary>
public sealed record BatchUploadResult
{
    /// <summary>
    /// Whether all files in the batch were uploaded successfully
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Unique identifier for this batch of files
    /// </summary>
    public required string BatchId { get; init; }

    /// <summary>
    /// Successfully uploaded files
    /// </summary>
    public ImmutableList<CloudFile> UploadedFiles { get; init; } = ImmutableList<CloudFile>.Empty;

    /// <summary>
    /// Failed uploads with their error messages
    /// </summary>
    public ImmutableDictionary<string, string> FailedFiles { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>
    /// Total number of files attempted
    /// </summary>
    public int TotalFiles => UploadedFiles.Count + FailedFiles.Count;

    /// <summary>
    /// Number of successfully uploaded files
    /// </summary>
    public int SuccessCount => UploadedFiles.Count;

    /// <summary>
    /// Number of failed uploads
    /// </summary>
    public int FailureCount => FailedFiles.Count;

    /// <summary>
    /// Duration of the batch upload operation
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Creates a successful batch upload result
    /// </summary>
    /// <param name="batchId">Batch identifier</param>
    /// <param name="files">Successfully uploaded files</param>
    /// <param name="duration">Duration of the operation</param>
    /// <returns>Successful batch result</returns>
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

    /// <summary>
    /// Creates a partial success batch upload result
    /// </summary>
    /// <param name="batchId">Batch identifier</param>
    /// <param name="successfulFiles">Successfully uploaded files</param>
    /// <param name="failedFiles">Files that failed with their error messages</param>
    /// <param name="duration">Duration of the operation</param>
    /// <returns>Partial success batch result</returns>
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

    /// <summary>
    /// Creates a failed batch upload result
    /// </summary>
    /// <param name="batchId">Batch identifier</param>
    /// <param name="failedFiles">All files that failed with their error messages</param>
    /// <param name="duration">Duration of the operation</param>
    /// <returns>Failed batch result</returns>
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
