// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Domain;

/// <summary>
/// Result of a file upload operation
/// </summary>
public sealed record UploadResult
{
    /// <summary>
    /// Whether the upload was successful
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// The uploaded file information (null if upload failed)
    /// </summary>
    public CloudFile? File { get; init; }

    /// <summary>
    /// Error message if upload failed
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Duration of the upload operation
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Creates a successful upload result
    /// </summary>
    /// <param name="file">The uploaded file</param>
    /// <param name="duration">Duration of the upload</param>
    /// <returns>Successful upload result</returns>
    public static UploadResult CreateSuccess(CloudFile file, TimeSpan duration = default)
        => new()
        {
            Success = true,
            File = file,
            Duration = duration
        };

    /// <summary>
    /// Creates a failed upload result
    /// </summary>
    /// <param name="errorMessage">Description of the failure</param>
    /// <param name="duration">Duration before failure</param>
    /// <returns>Failed upload result</returns>
    public static UploadResult CreateFailure(string errorMessage, TimeSpan duration = default)
        => new()
        {
            Success = false,
            ErrorMessage = errorMessage,
            Duration = duration
        };
}
