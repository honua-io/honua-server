// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
