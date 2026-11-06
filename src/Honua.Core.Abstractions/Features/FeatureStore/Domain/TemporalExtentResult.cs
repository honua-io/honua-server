// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

public readonly record struct TemporalExtentResult
{
    public DateTimeOffset? Start { get; init; }

    public DateTimeOffset? End { get; init; }

    public static TemporalExtentResult Create(DateTimeOffset? start, DateTimeOffset? end) => new()
    {
        Start = start,
        End = end
    };
}
