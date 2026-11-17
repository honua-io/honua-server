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
// work is O(n log n + limit) and the memory O(n) for n segments. Inside one comparison of two
// chains NTS also tests the envelopes of their halves; those tests are not counted, so one counted
// unit can cost up to a few hundred cheap envelope tests.
internal static class OverlayCandidatePairs
{
    // NTS compares two chains without a way to stop part-way, so a chain holds at most this many
    // segments and one comparison adds at most its square to the count.
    internal const int MaxChainSegments = 64;

    // Returns the pairs counted, or a number past the limit once the limit is passed.
    public static long Count(IReadOnlyList<Geometry> inputs, long limit)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        return CountLines(
            inputs.SelectMany(LinearComponentExtracter.GetLines).Select(static line => line.Coordinates),
            limit);
    }

    // The same count over lines given as coordinates, such as the curves a buffer is about to node.
    public static long CountLines(IEnumerable<Coordinate[]> lines, long limit)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var chains = BuildChains(lines);
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

    private static List<MonotoneChain> BuildChains(IEnumerable<Coordinate[]> lines)
    {
        var chains = new List<MonotoneChain>();
        foreach (var points in lines.Where(static points => points.Length > 1))
        {
            foreach (var chain in MonotoneChainBuilder.GetChains(points))
            {
                for (var start = chain.StartIndex; start < chain.EndIndex; start += MaxChainSegments)
                {
                    var end = Math.Min(start + MaxChainSegments, chain.EndIndex);
                    chains.Add(new MonotoneChain(points, start, end, null) { Id = chains.Count });
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

            // Each pair is counted once, from its lower chain. A visit from the higher chain, or to
            // itself, mirrors a counted one, so uncounted visits are at most the counted ones plus n.
            if (item.Id <= Current.Id)
            {
                return;
            }

            Pairs++;
            Current.ComputeOverlaps(item, this);
        }

        // The base action reads each candidate pair of segments and passes them here.
        public override void Overlap(LineSegment seg1, LineSegment seg2) => Pairs++;
    }
}
