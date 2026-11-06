// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Migration.Watermark;

public enum WatermarkKind
{
    EditTimestamp,

    FileModifiedTime
}

public sealed record SourceWatermark
{
    public required string PipelineId { get; init; }

    public required string SourceId { get; init; }

    public required WatermarkKind Kind { get; init; }

    // A null value means the source has never been extracted, so the first run is a full pull.
    public string? Value { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

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

    public static string Encode(DateTimeOffset instant)
        => instant.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
}
