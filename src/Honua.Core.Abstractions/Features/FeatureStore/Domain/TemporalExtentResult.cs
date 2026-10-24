// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// Represents the temporal extent (start/end) of features.
/// </summary>
public readonly record struct TemporalExtentResult
{
    /// <summary>
    /// Earliest timestamp found in the data.
    /// </summary>
    public DateTimeOffset? Start { get; init; }

    /// <summary>
    /// Latest timestamp found in the data.
    /// </summary>
    public DateTimeOffset? End { get; init; }

    /// <summary>
    /// Creates a temporal extent result.
    /// </summary>
    public static TemporalExtentResult Create(DateTimeOffset? start, DateTimeOffset? end) => new()
    {
        Start = start,
        End = end
    };
}
