// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// Defines how to bin features by a date field into temporal intervals
/// </summary>
public readonly record struct DateBinDefinition
{
    /// <summary>
    /// The date/timestamp field to bin on
    /// </summary>
    public required string BinField { get; init; }

    /// <summary>
    /// Calendar unit for calendar-based binning (e.g., "year", "month", "day", "hour")
    /// </summary>
    public string? CalendarUnit { get; init; }

    /// <summary>
    /// Fixed interval duration for interval-based binning
    /// </summary>
    public TimeSpan? FixedInterval { get; init; }

    /// <summary>
    /// Origin timestamp for fixed-interval binning alignment
    /// </summary>
    public DateTimeOffset? FixedIntervalOrigin { get; init; }

    /// <summary>
    /// Optional aggregate statistics to compute per bin
    /// </summary>
    public ImmutableArray<StatisticDefinition>? OutStatistics { get; init; }

    /// <summary>
    /// Whether this definition uses calendar-based binning
    /// </summary>
    public bool IsCalendarBin => !string.IsNullOrEmpty(CalendarUnit);
}
