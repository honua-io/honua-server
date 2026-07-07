// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

/// <summary>
/// How an import load reconciles with an existing target table.
/// </summary>
public enum ImportLoadMode
{
    /// <summary>
    /// Full replace. The target is rebuilt from the incoming features. Implemented as a
    /// transactional staging-table swap so a failed or cancelled load never leaves a
    /// half-dropped target. This is the historical behavior and the default; it is what
    /// <see cref="ImportRequest.OverwriteExisting"/> maps to.
    /// </summary>
    Replace = 0,

    /// <summary>
    /// Append. Incoming features are inserted into the existing target without dropping it.
    /// The target is created if it does not yet exist.
    /// </summary>
    Append = 1,

    /// <summary>
    /// Keyed upsert/merge. Incoming features whose <see cref="ImportRequest.KeyColumns"/>
    /// value(s) match an existing row UPDATE that row in place; the rest are inserted. The
    /// target is never dropped, so an incremental update preserves untouched rows. Requires
    /// at least one key column.
    /// </summary>
    Upsert = 2,
}

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
    /// Stable source kind for admin import job result views.
    /// </summary>
    public string SourceKind { get; init; } = "file";

    /// <summary>
    /// Source URL when the import was accepted from a remote object URL.
    /// </summary>
    public string? SourceUrl { get; init; }

    /// <summary>
    /// Upload operation ID associated with this import, when supplied by the client or storage provider.
    /// </summary>
    public string? UploadId { get; init; }

    /// <summary>
    /// Target table name in PostgreSQL
    /// </summary>
    public required string TableName { get; init; }

    /// <summary>
    /// Optional target schema for imported operational data.
    /// When omitted, the configured PostgreSQL operational-data schema is used.
    /// </summary>
    public string? TargetSchema { get; init; }

    /// <summary>
    /// Source coordinate reference system ID (detected or specified)
    /// </summary>
    public int? SourceSrid { get; init; }

    /// <summary>
    /// Target coordinate reference system ID (for transformation)
    /// </summary>
    public int TargetSrid { get; init; } = 4326;

    /// <summary>
    /// Whether to overwrite existing table.
    /// </summary>
    /// <remarks>
    /// Preserved for backward compatibility. It is equivalent to
    /// <see cref="ImportLoadMode.Replace"/> and only takes effect when
    /// <see cref="LoadMode"/> is not explicitly set away from the default
    /// (<see cref="EffectiveLoadMode"/> resolves the two).
    /// </remarks>
    public bool OverwriteExisting { get; init; }

    /// <summary>
    /// How the load reconciles with an existing target table (replace / append / upsert).
    /// Defaults to <see cref="ImportLoadMode.Replace"/>, matching the historical
    /// full-replace behavior and <see cref="OverwriteExisting"/>.
    /// </summary>
    public ImportLoadMode LoadMode { get; init; } = ImportLoadMode.Replace;

    /// <summary>
    /// One or more property key names used as the conflict target for
    /// <see cref="ImportLoadMode.Upsert"/>. Required when <see cref="EffectiveLoadMode"/>
    /// resolves to <see cref="ImportLoadMode.Upsert"/>; ignored for replace/append.
    /// </summary>
    public IReadOnlyList<string> KeyColumns { get; init; } = [];

    /// <summary>
    /// Optional CSV-specific options (explicit longitude/latitude column names or an
    /// address column resolved through a caller-supplied geocoder). Ignored for
    /// non-CSV formats. When omitted the CSV reader's header auto-detection applies.
    /// </summary>
    public CsvImportOptions? CsvOptions { get; init; }

    /// <summary>
    /// The resolved load mode, reconciling the legacy <see cref="OverwriteExisting"/> flag
    /// with the explicit <see cref="LoadMode"/>. <see cref="LoadMode"/> wins when it is set
    /// to a non-default value; otherwise <see cref="OverwriteExisting"/> selects replace.
    /// </summary>
    public ImportLoadMode EffectiveLoadMode =>
        LoadMode != ImportLoadMode.Replace ? LoadMode : ImportLoadMode.Replace;

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

        if (EffectiveLoadMode == ImportLoadMode.Upsert && KeyColumns.Count == 0)
        {
            throw new InvalidOperationException("Upsert load mode requires at least one key column.");
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
