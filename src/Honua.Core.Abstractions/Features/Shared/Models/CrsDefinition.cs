// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Shared.Models;

public enum AxisOrder
{
    EastNorth,

    NorthEast
}

public readonly record struct CrsDefinition(string Uri, int Srid, AxisOrder AxisOrder, bool IsGeographic)
{
    public string? Wkt { get; init; }

    public double? LinearUnitFactor { get; init; }
}
