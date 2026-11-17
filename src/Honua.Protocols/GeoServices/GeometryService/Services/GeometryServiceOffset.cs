// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Protocols.GeoServices.GeometryService.Models;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Buffer;

namespace Honua.Protocols.GeoServices.GeometryService.Services;

// Both offsets are drawn by buffering, which nodes curves that can cross far more often than the
// input does; the buffer is counted against the overlay budget as it nodes them.
internal static class GeometryServiceOffset
{
    public static Geometry Draw(Geometry geometry, JoinStyle joinStyle, OffsetParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(parameters);

        var distance = parameters.OffsetDistance;
        if (geometry is IPolygonal)
        {
            return parameters.OverlayBudget.Buffer(geometry, distance, new BufferParameters());
        }

        // OffsetCurve buffers the line by the distance and keeps the side of the outline it needs;
        // the same buffer is built under the budget first.
        var quadrantSegments = BufferParameters.DefaultQuadrantSegments;
        var mitreLimit = Math.Max(parameters.BevelRatio, BufferParameters.DefaultMitreLimit);
        parameters.OverlayBudget.Buffer(
            geometry,
            Math.Abs(distance),
            new BufferParameters(quadrantSegments, EndCapStyle.Round, joinStyle, mitreLimit));
        return OffsetCurve.GetCurve(geometry, -distance, quadrantSegments, joinStyle, mitreLimit);
    }
}
