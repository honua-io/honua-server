// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

// A page of the collapsed changes in (SinceGeneration, ThroughGeneration], oldest first, at most Limit long.
public sealed record FeatureChangeWindow
{
    public required long SinceGeneration { get; init; }

    public required long ThroughGeneration { get; init; }

    public required int[] LayerIds { get; init; }

    public required int Limit { get; init; }
}
