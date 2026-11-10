// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Geoprocessing.LocalRunner;

public sealed record LocalRunPhase
{
    public required string Phase { get; init; }

    public double? PercentComplete { get; init; }

    public TimeSpan Elapsed { get; init; }
}
