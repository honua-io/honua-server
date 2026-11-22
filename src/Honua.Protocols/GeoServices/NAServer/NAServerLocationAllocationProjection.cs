// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Routing.Features.Routing.Domain;

namespace Honua.Protocols.GeoServices.NAServer;

internal static class NAServerLocationAllocationProjection
{
    public static async Task<LocationAllocationSolveRequest?> ProjectOutputAsync(
        LocationAllocationSolveRequest request,
        ICoordinateTransformService transformService,
        CancellationToken cancellationToken)
    {
        if (request.InSrid == request.OutSrid)
        {
            return request;
        }

        var points = request.Facilities.Concat(request.DemandPoints.Select(demand => demand.Location)).ToArray();
        var xs = points.Select(point => point.Lon).ToArray();
        var ys = points.Select(point => point.Lat).ToArray();
        if (!await transformService.TransformPointsAsync(xs, ys, request.InSrid, request.OutSrid, cancellationToken)
            .ConfigureAwait(false))
        {
            return null;
        }

        return request with
        {
            Facilities = request.Facilities.Select((_, index) => new RoutePoint(xs[index], ys[index])).ToArray(),
            DemandPoints = request.DemandPoints.Select((demand, index) => demand with
            {
                Location = new RoutePoint(xs[request.Facilities.Count + index], ys[request.Facilities.Count + index])
            }).ToArray(),
            InSrid = request.OutSrid
        };
    }
}
