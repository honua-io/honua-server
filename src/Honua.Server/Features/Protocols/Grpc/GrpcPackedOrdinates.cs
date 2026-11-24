// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Google.Protobuf.Collections;
using NetTopologySuite.Geometries;
using Proto = Geospatial.V1;

namespace Honua.Server.Features.Protocols.Grpc;

// Decodes geospatial.v1 packed ordinates [x0, y0, (z0), (m0), x1, ...]. Stride 3 is XYZ unless
// has_m alone is set; stride 0 is derived from has_z/has_m.
internal static class GrpcPackedOrdinates
{
    public static Coordinate[] Decode(Proto.CoordinateSequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        var hasM = sequence.HasM || sequence.Stride == 4;
        var hasZ = sequence.HasZ || sequence.Stride == 4 || (sequence.Stride == 3 && !hasM);
        var layout = (hasZ, hasM) switch
        {
            (true, true) => Layout.Xyzm,
            (true, false) => Layout.Xyz,
            (false, true) => Layout.Xym,
            _ => Layout.Xy,
        };
        var stride = layout is Layout.Xyzm ? 4 : layout is Layout.Xy ? 2 : 3;
        var ordinates = sequence.Ordinates;
        if ((sequence.Stride != 0 && sequence.Stride != stride) || ordinates.Count % stride != 0)
        {
            throw GrpcConversionHelpers.InvalidArgument(
                "Packed ordinates must match stride, has_z and has_m, with a whole number of vertices.");
        }

        return Enumerable.Range(0, ordinates.Count / stride)
            .Select(vertex => CreateCoordinate(ordinates, vertex * stride, layout))
            .ToArray();
    }

    private static Coordinate CreateCoordinate(RepeatedField<double> ordinates, int offset, Layout layout)
    {
        var x = ordinates[offset];
        var y = ordinates[offset + 1];
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            throw GrpcConversionHelpers.InvalidArgument("Geometry coordinates must be finite numbers.");
        }

        return layout switch
        {
            Layout.Xyzm => new CoordinateZM(x, y, ordinates[offset + 2], ordinates[offset + 3]),
            Layout.Xyz => new CoordinateZ(x, y, ordinates[offset + 2]),
            Layout.Xym => new CoordinateM(x, y, ordinates[offset + 2]),
            _ => new Coordinate(x, y),
        };
    }

    private enum Layout
    {
        Xy,
        Xyz,
        Xym,
        Xyzm,
    }
}
