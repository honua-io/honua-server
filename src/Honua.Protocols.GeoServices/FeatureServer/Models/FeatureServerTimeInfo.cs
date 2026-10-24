// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

/// <summary>
/// Time information for FeatureServer layers.
/// </summary>
public sealed class FeatureServerTimeInfo
{
    /// <summary>
    /// Field name containing start time values.
    /// </summary>
    public string? StartTimeField { get; init; }

    /// <summary>
    /// Field name containing end time values (optional for interval data).
    /// </summary>
    public string? EndTimeField { get; init; }

    /// <summary>
    /// Optional track identifier field.
    /// </summary>
    public string? TrackIdField { get; init; }

    /// <summary>
    /// Temporal extent in milliseconds since epoch (min, max).
    /// </summary>
    public long?[]? TimeExtent { get; init; }
}
