// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

// Min and max are null when the layer is time-enabled but no rows have been ingested.
public sealed class TemporalExtentResponse
{
    public int LayerId { get; init; }

    public string? LayerName { get; init; }

    public string? StartTimeField { get; init; }

    // Null when the layer represents instants only.
    public string? EndTimeField { get; init; }

    public string? Min { get; init; }

    public string? Max { get; init; }

    public long? MinEpochMs { get; init; }

    public long? MaxEpochMs { get; init; }
}
