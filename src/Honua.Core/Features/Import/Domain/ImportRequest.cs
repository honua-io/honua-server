// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

/// <summary>
/// Request to import a geospatial file into a layer
/// </summary>
public sealed record ImportRequest
{
    /// <summary>
    /// File stream to import (optional if CloudFileId is provided)
    /// </summary>
    public Stream? FileStream { get; init; }

    /// <summary>
    /// Cloud storage file ID (optional if FileStream is provided)
    /// </summary>
    public string? CloudFileId { get; init; }

    /// <summary>
    /// Local staged file path (optional if FileStream or CloudFileId is provided).
    /// Used for streamed ingest paths that spool to disk before import processing.
    /// </summary>
    public string? LocalFilePath { get; init; }

    /// <summary>
    /// Original filename (used for format detection)
    /// </summary>
    public required string FileName { get; init; }

    /// <summary>
    /// Target table name in PostgreSQL
    /// </summary>
    public required string TableName { get; init; }

    /// <summary>
    /// Source coordinate reference system ID (detected or specified)
    /// </summary>
    public int? SourceSrid { get; init; }

    /// <summary>
    /// Target coordinate reference system ID (for transformation)
    /// </summary>
    public int TargetSrid { get; init; } = 4326;

    /// <summary>
    /// Whether to overwrite existing table
    /// </summary>
    public bool OverwriteExisting { get; init; }

    /// <summary>
    /// Validates that either FileStream or CloudFileId is provided
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when neither FileStream nor CloudFileId is provided</exception>
    public void Validate()
    {
        var cloudFileId = string.IsNullOrWhiteSpace(CloudFileId) ? null : CloudFileId;
        var localFilePath = string.IsNullOrWhiteSpace(LocalFilePath) ? null : LocalFilePath;
        var sourceCount = 0;

        if (FileStream != null)
        {
            sourceCount++;
        }

        if (cloudFileId != null)
        {
            sourceCount++;
        }

        if (localFilePath != null)
        {
            sourceCount++;
        }

        if (sourceCount == 0)
        {
            throw new InvalidOperationException("Either FileStream, CloudFileId, or LocalFilePath must be provided.");
        }

        if (sourceCount > 1)
        {
            throw new InvalidOperationException("Only one of FileStream, CloudFileId, or LocalFilePath should be provided, not multiple sources.");
        }
    }

    /// <summary>
    /// Gets a value indicating whether this request uses cloud storage
    /// </summary>
    public bool UsesCloudStorage => !string.IsNullOrWhiteSpace(CloudFileId);

    /// <summary>
    /// Gets a value indicating whether this request uses a local staged file path.
    /// </summary>
    public bool UsesLocalFile => !string.IsNullOrWhiteSpace(LocalFilePath);
}
