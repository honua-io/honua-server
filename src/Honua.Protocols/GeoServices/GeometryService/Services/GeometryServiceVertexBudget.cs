// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Protocols.GeoServices.GeometryService.Services;

// Vertices that densify and shape-preserving measurement insert. Their number follows a
// length and a spacing, not the size of the request. One geometry may gain as many vertices
// as the server accepts in a geometry (Limits:Geometry:MaxVerticesPerGeometry). A densify
// request may gain a few geometries' worth in total for a signed-in caller and one for a
// caller that is not signed in.
internal sealed class GeometryServiceVertexBudget
{
    internal const int SignedInGeometriesPerRequest = 4;

    // The upper end of the option's valid range, applied here as well so that an unvalidated
    // configuration cannot widen the budget.
    internal const int PerGeometryCeiling = 100_000;

    private static readonly int _defaultPerGeometry = new GeometryLimits().MaxVerticesPerGeometry;

    private double _generated;

    internal GeometryServiceVertexBudget(int maxVerticesPerGeometry, GeometryServiceCaller caller)
    {
        PerGeometry = Math.Max(maxVerticesPerGeometry, 1);
        PerRequest = caller == GeometryServiceCaller.SignedIn
            ? (long)PerGeometry * SignedInGeometriesPerRequest
            : PerGeometry;
    }

    public int PerGeometry { get; }

    public long PerRequest { get; }

    public static GeometryServiceVertexBudget Create(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var configured = context.RequestServices
            .GetService<IOptions<LimitsOptions>>()?.Value.Geometry.MaxVerticesPerGeometry;
        var perGeometry = configured is > 0
            ? Math.Min(configured.Value, PerGeometryCeiling)
            : _defaultPerGeometry;
        return new GeometryServiceVertexBudget(perGeometry, GeometryServiceCallers.Of(context));
    }

    // True when one geometry may gain this many vertices. Nothing is counted: measurement
    // builds, measures and drops one geometry at a time.
    public bool FitsOneGeometry(double estimatedVertices)
        => double.IsFinite(estimatedVertices) && estimatedVertices >= 0 && estimatedVertices <= PerGeometry;

    // Reserves the vertices one geometry of a densify request will gain. False leaves the
    // budget unchanged: the caller refuses the request before generating anything.
    public bool TryReserve(double estimatedVertices)
    {
        if (!FitsOneGeometry(estimatedVertices))
        {
            return false;
        }

        var total = _generated + estimatedVertices;
        if (total > PerRequest)
        {
            return false;
        }

        _generated = total;
        return true;
    }
}
