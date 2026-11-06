// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Migration.Watermark;

public static class WatermarkExtractPlanner
{
    public static GeoServicesIncrementalQuery PlanEsri(SourceWatermark? watermark, string? editField)
    {
        var since = watermark?.Kind == WatermarkKind.EditTimestamp ? watermark.AsTimestamp() : null;
        if (since is null || string.IsNullOrWhiteSpace(editField))
        {
            return new GeoServicesIncrementalQuery
            {
                IsIncremental = false,
                WhereClause = "1=1"
            };
        }

        var epochMs = since.Value.ToUnixTimeMilliseconds();
        var where = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{editField} > {epochMs}");
        return new GeoServicesIncrementalQuery
        {
            IsIncremental = true,
            WhereClause = where,
            SinceEpochMilliseconds = epochMs
        };
    }

    // The OGC interval starts one millisecond after the mark, because the datetime interval is inclusive and would otherwise re-pull the boundary record.
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

    public static bool ShouldPullFile(SourceWatermark? watermark, DateTimeOffset fileModifiedAt)
    {
        var since = watermark?.Kind == WatermarkKind.FileModifiedTime ? watermark.AsTimestamp() : null;
        return since is null || fileModifiedAt.ToUniversalTime() > since.Value;
    }

    // The stored watermark only moves forward; a batch with no newer record keeps the previous mark.
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

public readonly record struct GeoServicesIncrementalQuery
{
    public required bool IsIncremental { get; init; }

    public required string WhereClause { get; init; }

    public long? SinceEpochMilliseconds { get; init; }
}

public readonly record struct OgcTemporalFilter
{
    public required bool IsIncremental { get; init; }

    public string? DatetimeParameter { get; init; }

    public string? DatetimeField { get; init; }
}
