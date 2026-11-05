// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.FeatureStore.Domain;

public readonly record struct DateBinDefinition
{
    public required string BinField { get; init; }

    public string? CalendarUnit { get; init; }

    public TimeSpan? FixedInterval { get; init; }

    public DateTimeOffset? FixedIntervalOrigin { get; init; }

    public ImmutableArray<StatisticDefinition>? OutStatistics { get; init; }

    public bool IsCalendarBin => !string.IsNullOrEmpty(CalendarUnit);
}
