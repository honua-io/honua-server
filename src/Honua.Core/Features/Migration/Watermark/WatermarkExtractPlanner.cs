// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Migration.Watermark;

/// <summary>
/// Translates a persisted <see cref="SourceWatermark"/> into the source-specific filter a
/// remote pull source applies to fetch only records that changed since the high-water mark.
/// Pure and AOT-safe (no reflection): each method maps a watermark into a small, serialisable
/// instruction the corresponding import adapter (Esri FeatureServer, OGC API Features / WFS,
/// file/object pull) applies before pulling.
/// </summary>
public static class WatermarkExtractPlanner
{
    /// <summary>
    /// Builds the Esri FeatureServer incremental query for a layer that exposes edit tracking.
    /// When the watermark is unset (first run) a full pull is planned; otherwise a
    /// <c>{editField} &gt; {epochMs}</c> where-clause restricts the pull to edited features.
    /// Esri encodes <c>lastEditDate</c> as epoch milliseconds, so the watermark instant is
    /// converted to epoch-ms for the predicate.
    /// </summary>
    /// <param name="watermark">Current watermark, or <c>null</c> for first run.</param>
    /// <param name="editField">
    /// Edit-tracking field from the layer's <c>editFieldsInfo</c> (e.g. <c>last_edited_date</c>).
    /// Required when an incremental pull is wanted; if null/blank a full pull is planned.
    /// </param>
    public static EsriIncrementalQuery PlanEsri(SourceWatermark? watermark, string? editField)
    {
        var since = watermark?.Kind == WatermarkKind.EditTimestamp ? watermark.AsTimestamp() : null;
        if (since is null || string.IsNullOrWhiteSpace(editField))
        {
            return new EsriIncrementalQuery
            {
                IsIncremental = false,
                WhereClause = "1=1"
            };
        }

        var epochMs = since.Value.ToUnixTimeMilliseconds();
        var where = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{editField} > {epochMs}");
        return new EsriIncrementalQuery
        {
            IsIncremental = true,
            WhereClause = where,
            SinceEpochMilliseconds = epochMs
        };
    }

    /// <summary>
    /// Builds the OGC API Features / WFS temporal filter for an incremental pull. When the
    /// watermark is set, returns a half-open <c>datetime</c> interval starting just after the
    /// high-water mark (OGC datetime intervals are inclusive, so the mark is nudged forward by
    /// one millisecond to avoid re-pulling the boundary record). A null watermark plans a full
    /// pull.
    /// </summary>
    public static OgcTemporalFilter PlanOgc(SourceWatermark? watermark, string? datetimeField = null)
    {
        var since = watermark?.Kind == WatermarkKind.EditTimestamp ? watermark.AsTimestamp() : null;
        if (since is null)
        {
            return new OgcTemporalFilter { IsIncremental = false };
        }

        var lowerBound = since.Value.AddMilliseconds(1);
        var datetime = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{lowerBound.ToUniversalTime():O}/..");
        return new OgcTemporalFilter
        {
            IsIncremental = true,
            DatetimeParameter = datetime,
            DatetimeField = datetimeField
        };
    }

    /// <summary>
    /// Decides whether a file/object should be pulled given the current file-mtime watermark.
    /// Only files modified strictly after the mark are re-pulled; a null watermark pulls every
    /// file (full scan).
    /// </summary>
    public static bool ShouldPullFile(SourceWatermark? watermark, DateTimeOffset fileModifiedAt)
    {
        var since = watermark?.Kind == WatermarkKind.FileModifiedTime ? watermark.AsTimestamp() : null;
        return since is null || fileModifiedAt.ToUniversalTime() > since.Value;
    }

    /// <summary>
    /// Computes the watermark to persist after a successful incremental pull, given the maximum
    /// edit/modified timestamp observed across the pulled records. The watermark only advances
    /// (never rewinds): if the batch produced no newer records the prior watermark is retained.
    /// </summary>
    public static SourceWatermark Advance(
        SourceWatermark current,
        DateTimeOffset? maxObservedTimestamp,
        DateTimeOffset now)
    {
        var existing = current.AsTimestamp();
        if (maxObservedTimestamp is null)
        {
            return current with { UpdatedAt = now };
        }

        var observed = maxObservedTimestamp.Value.ToUniversalTime();
        if (existing is { } prior && observed <= prior)
        {
            return current with { UpdatedAt = now };
        }

        return current with
        {
            Value = SourceWatermark.Encode(observed),
            UpdatedAt = now
        };
    }
}

/// <summary>
/// Esri FeatureServer incremental query instruction derived from a watermark.
/// </summary>
public readonly record struct EsriIncrementalQuery
{
    /// <summary>
    /// Whether the pull is incremental (changed-since) or a full scan.
    /// </summary>
    public required bool IsIncremental { get; init; }

    /// <summary>
    /// Where-clause to send as the FeatureServer <c>where</c> parameter.
    /// </summary>
    public required string WhereClause { get; init; }

    /// <summary>
    /// Lower-bound edit time as epoch milliseconds, when incremental.
    /// </summary>
    public long? SinceEpochMilliseconds { get; init; }
}

/// <summary>
/// OGC API Features / WFS temporal filter instruction derived from a watermark.
/// </summary>
public readonly record struct OgcTemporalFilter
{
    /// <summary>
    /// Whether the pull is incremental (changed-since) or a full scan.
    /// </summary>
    public required bool IsIncremental { get; init; }

    /// <summary>
    /// Value for the OGC API Features <c>datetime</c> query parameter (half-open interval),
    /// when incremental.
    /// </summary>
    public string? DatetimeParameter { get; init; }

    /// <summary>
    /// Optional source temporal field the filter applies to (for WFS temporal predicates).
    /// </summary>
    public string? DatetimeField { get; init; }
}
