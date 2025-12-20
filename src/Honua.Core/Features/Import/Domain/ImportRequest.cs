// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

/// <summary>
/// Request to import a geospatial file into a layer
/// </summary>
public sealed record ImportRequest
{
    /// <summary>
    /// File stream to import
    /// </summary>
    public required Stream FileStream { get; init; }

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
}
