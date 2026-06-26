// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Migration.Watermark;

/// <summary>
/// Kind of high-water mark a remote pull source advances for incremental ("changed-since")
/// extraction. The kind selects how the persisted <see cref="SourceWatermark.Value"/> is
/// translated into a source-specific filter (Esri <c>lastEditDate</c>, OGC/WFS temporal,
/// or file mtime) and how it is compared/advanced.
/// </summary>
public enum WatermarkKind
{
    /// <summary>
    /// Edit timestamp high-water mark. The persisted value is a UTC instant (round-trip
    /// "O" format). Esri sources translate it into a <c>lastEditDate &gt; value</c> predicate
    /// over the layer's edit-tracking field; OGC/WFS sources translate it into a temporal
    /// <c>datetime</c> / <c>after</c> filter.
    /// </summary>
    EditTimestamp,

    /// <summary>
    /// File modified-time high-water mark for file/object pull sources. The persisted value is
    /// a UTC instant (round-trip "O" format); only files whose mtime is strictly greater are
    /// re-pulled.
    /// </summary>
    FileModifiedTime
}

/// <summary>
/// Durable high-water mark for one pipeline+source pair. A scheduled run reads the watermark,
/// pulls only records that changed after it, then advances the mark on success. Recovery
/// resumes from the persisted mark rather than re-scanning the whole source.
/// </summary>
public sealed record SourceWatermark
{
    /// <summary>
    /// Pipeline (workflow / import job) that owns the watermark.
    /// </summary>
    public required string PipelineId { get; init; }

    /// <summary>
    /// Stable identifier of the source within the pipeline (e.g. the source layer/collection
    /// URL or file path). Lets one pipeline track several sources independently.
    /// </summary>
    public required string SourceId { get; init; }

    /// <summary>
    /// Watermark kind, selecting the source-specific translation/compare semantics.
    /// </summary>
    public required WatermarkKind Kind { get; init; }

    /// <summary>
    /// Opaque high-water value. For <see cref="WatermarkKind.EditTimestamp"/> and
    /// <see cref="WatermarkKind.FileModifiedTime"/> this is a UTC instant in round-trip
    /// "O" format. <c>null</c> means "never extracted" — the first run performs a full pull
    /// and then seeds the mark.
    /// </summary>
    public string? Value { get; init; }

    /// <summary>
    /// When the watermark was last advanced.
    /// </summary>
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>
    /// Parses <see cref="Value"/> as a UTC instant for timestamp-based watermark kinds, or
    /// <c>null</c> when unset/unparseable (treated as "no lower bound", i.e. full pull).
    /// </summary>
    public DateTimeOffset? AsTimestamp()
    {
        if (string.IsNullOrWhiteSpace(Value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            Value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    /// <summary>
    /// Encodes a UTC instant into the canonical round-trip watermark value.
    /// </summary>
    public static string Encode(DateTimeOffset instant)
        => instant.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
}
