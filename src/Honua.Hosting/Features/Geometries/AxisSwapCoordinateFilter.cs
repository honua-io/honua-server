// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using NetTopologySuite.Geometries;

namespace Honua.Infrastructure.Geometries;

internal sealed class AxisSwapCoordinateFilter : ICoordinateSequenceFilter
{
    public bool Done => false;

    public bool GeometryChanged => true;

    public void Filter(CoordinateSequence seq, int i)
    {
        var x = seq.GetX(i);
        var y = seq.GetY(i);
        seq.SetX(i, y);
        seq.SetY(i, x);
    }
}
