// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.Index;
using NetTopologySuite.Index.Chain;
using NetTopologySuite.Index.HPRtree;

namespace Honua.Protocols.GeoServices.GeometryService.Services;

// Counts the pairs of segments that could cross, within and between the given geometries.
// Segments are grouped into monotone chains, which cannot cross themselves, and the chains are
// indexed by envelope. Every chain whose envelope a query meets, and every pair of segments
// whose envelopes overlap, is one unit of work; every crossing lies in one such pair, so the
// count bounds the crossings an engine can find. Counting stops once the limit is passed: the
// work is O(n log n + limit) and the memory O(n) for n segments.
internal static class OverlayCandidatePairs
{
    // NTS compares two chains without a way to stop part-way, so a chain holds at most this many
    // segments and one comparison adds at most its square to the count.
    internal const int MaxChainSegments = 64;

    // Returns the pairs counted, or a number past the limit once the limit is passed.
    public static long Count(IReadOnlyList<Geometry> inputs, long limit)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var chains = BuildChains(inputs);
        var index = new HPRtree<MonotoneChain>();
        foreach (var chain in chains)
        {
            index.Insert(chain.Envelope, chain);
        }

        index.Build();
        var counter = new Counter(limit);
        foreach (var chain in chains)
        {
            if (counter.Spent)
            {
                break;
            }

            counter.Current = chain;
            index.Query(chain.Envelope, counter);
        }

        return counter.Pairs;
    }

    private static List<MonotoneChain> BuildChains(IReadOnlyList<Geometry> inputs)
    {
        var chains = new List<MonotoneChain>();
        foreach (var input in inputs)
        {
            foreach (var line in LinearComponentExtracter.GetLines(input).Cast<LineString>())
            {
                var points = line.Coordinates;
                foreach (var chain in MonotoneChainBuilder.GetChains(points))
                {
                    for (var start = chain.StartIndex; start < chain.EndIndex; start += MaxChainSegments)
                    {
                        var end = Math.Min(start + MaxChainSegments, chain.EndIndex);
                        chains.Add(new MonotoneChain(points, start, end, null) { Id = chains.Count });
                    }
                }
            }
        }

        return chains;
    }

    private sealed class Counter(long limit) : MonotoneChainOverlapAction, IItemVisitor<MonotoneChain>
    {
        public MonotoneChain Current { get; set; } = null!;

        public long Pairs { get; private set; }

        public bool Spent => Pairs > limit;

        // The index has no way to stop a query part-way; once the limit is passed the rest of
        // the query is skipped here, which costs at most one visit per chain.
        public void VisitItem(MonotoneChain item)
        {
            if (Spent)
            {
                return;
            }

            Pairs++;
            if (item.Id > Current.Id)
            {
                Current.ComputeOverlaps(item, this);
            }
        }

        // The base action reads each candidate pair of segments and passes them here.
        public override void Overlap(LineSegment seg1, LineSegment seg2) => Pairs++;
    }
}
