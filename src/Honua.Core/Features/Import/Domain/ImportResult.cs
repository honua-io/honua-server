// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

/// <summary>
/// Result of a file import operation
/// </summary>
public sealed record ImportResult
{
    /// <summary>
    /// Whether the import was successful
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Number of features imported
    /// </summary>
    public int FeatureCount { get; init; }

    /// <summary>
    /// Table name created/updated
    /// </summary>
    public required string TableName { get; init; }

    /// <summary>
    /// Detected file format
    /// </summary>
    public required SupportedFileFormat Format { get; init; }

    /// <summary>
    /// Detected coordinate reference system ID
    /// </summary>
    public int? DetectedSrid { get; init; }

    /// <summary>
    /// Error message if import failed
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Import duration
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Create successful import result
    /// </summary>
    public static ImportResult CreateSuccess(
        string tableName,
        SupportedFileFormat format,
        int featureCount,
        int? detectedSrid = null,
        TimeSpan duration = default) =>
        new()
        {
            Success = true,
            TableName = tableName,
            Format = format,
            FeatureCount = featureCount,
            DetectedSrid = detectedSrid,
            Duration = duration
        };

    /// <summary>
    /// Create failed import result
    /// </summary>
    public static ImportResult CreateFailure(
        string tableName,
        SupportedFileFormat format,
        string errorMessage,
        TimeSpan duration = default) =>
        new()
        {
            Success = false,
            TableName = tableName,
            Format = format,
            ErrorMessage = errorMessage,
            Duration = duration
        };
}
