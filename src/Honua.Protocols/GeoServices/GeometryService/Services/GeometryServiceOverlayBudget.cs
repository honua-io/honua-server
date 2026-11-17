// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetTopologySuite.Algorithm;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NetTopologySuite.Noding;
using NetTopologySuite.Operation.Buffer;

namespace Honua.Protocols.GeoServices.GeometryService.Services;

// Overlay, union, cut, repair and relation node their inputs: every place where two segments
// cross becomes a vertex, and the engine holds all of them at once. Inputs within the vertex
// limits can still cross each other about n²/4 times. Before one of these operations runs, the
// pairs of segments that could cross are counted (Limits:Geometry:MaxOverlayCandidatePairs, a
// quarter of it for a caller that is not signed in), and the operation is refused past that
// number before the engine is called. Each engine call is checked on its own: the arrangement
// of one call is released before the next is built.
internal sealed class GeometryServiceOverlayBudget
{
    internal const int NotSignedInDivisor = 4;

    // The upper end of the option's valid range, applied here as well so that an unvalidated
    // configuration cannot widen the budget.
    internal const long PerOperationCeiling = 50_000_000;

    internal const string RefusalMessage =
        "The geometries cross each other in more places than this service computes in one operation. Send simpler or fewer geometries.";

    private static readonly long _defaultPerOperation = new GeometryLimits().MaxOverlayCandidatePairs;

    internal GeometryServiceOverlayBudget(long perOperation)
    {
        PerOperation = Math.Max(perOperation, 1);
    }

    public long PerOperation { get; }

    public static GeometryServiceOverlayBudget Create(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var configured = context.RequestServices
            .GetService<IOptions<LimitsOptions>>()?.Value.Geometry.MaxOverlayCandidatePairs;
        var perOperation = configured is > 0
            ? Math.Min(configured.Value, PerOperationCeiling)
            : _defaultPerOperation;
        return new GeometryServiceOverlayBudget(
            GeometryServiceCallers.Of(context) == GeometryServiceCaller.SignedIn
                ? perOperation
                : perOperation / NotSignedInDivisor);
    }

    // Refuses the operation when its inputs could cross in more places than the budget allows.
    public void Check(params Geometry[] inputs)
    {
        if (OverlayCandidatePairs.Count(inputs, PerOperation) > PerOperation)
        {
            throw new GeometryServiceLimitException(RefusalMessage);
        }
    }

    public void Check(params byte[][] inputWkbs)
    {
        ArgumentNullException.ThrowIfNull(inputWkbs);
        var reader = new WKBReader();
        Check(Array.ConvertAll(inputWkbs, reader.Read));
    }

    // A buffer nodes the offset curves it draws around its input, not the input, and those curves
    // can cross each other far more often than the input does. The curves are counted when the
    // buffer is about to node them, and the buffer is refused past the budget.
    public Geometry Buffer(Geometry geometry, double distance, BufferParameters bufferParameters)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        var builder = new BufferBuilder(bufferParameters) { Noder = new BudgetedNoder(PerOperation) };
        try
        {
            return builder.Buffer(geometry, distance);
        }
        catch (TopologyException)
        {
            // The full buffer retries with snapped coordinates when floating-point noding fails.
            // It nodes the same curves, which have just been counted within the budget.
            return BufferOp.Buffer(geometry, distance, bufferParameters);
        }
    }

    private sealed class BudgetedNoder(long limit) : INoder
    {
        private readonly MCIndexNoder _noder = new(new IntersectionAdder(new RobustLineIntersector()));

        public void ComputeNodes(IList<ISegmentString> segStrings)
        {
            ArgumentNullException.ThrowIfNull(segStrings);
            if (OverlayCandidatePairs.CountLines(segStrings.Select(static curve => curve.Coordinates), limit) > limit)
            {
                throw new GeometryServiceLimitException(RefusalMessage);
            }

            _noder.ComputeNodes(segStrings);
        }

        public IList<ISegmentString> GetNodedSubstrings() => _noder.GetNodedSubstrings();
    }
}
