// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// <c>analytics.cluster-managed</c> executor. A job-dispatchable, managed
/// (NetTopologySuite) spatial-clustering counterpart to trunk's
/// <c>analytics.cluster</c>, which runs only synchronously through the
/// layer-scoped PostGIS <c>SpatialAnalytics</c> protocol (Pro-gated, not routed
/// through the geoprocessing job dispatcher). Like
/// <see cref="ManagedSpatialJoinExecutor"/>, this is the
/// workflow/codemod-reachable counterpart that runs against an INLINE
/// FeatureCollection (no Postgres dependency) so the lean dispatcher can
/// construct it unconditionally.
///
/// Supports DBSCAN and K-Means modes. Each input feature is preserved
/// one-to-one with a <c>CLUSTER_ID</c> attribute appended; DBSCAN noise points
/// are emitted with <c>CLUSTER_ID = -1</c>. Distances are evaluated in the
/// CRS units of the supplied feature geometries (the same convention used by
/// the managed <c>geometry.buffer</c> executor — geodesic distance requires a
/// CRS authority lookup that the lean dispatcher cannot perform).
///
/// Features whose geometry is null or empty are dropped before clustering and
/// are not emitted. Non-point geometries cluster on their centroid.
/// </summary>
internal sealed class ManagedClusterExecutor(
    IOptionsMonitor<GeoprocessingExecutorOptions> options)
    : FeatureCollectionTransformExecutor(options)
{
    internal const string HandledProcessId = "analytics.cluster-managed";
    internal const string ClusterIdAttribute = "CLUSTER_ID";
    internal const int NoiseClusterId = -1;
    private const int MaxKMeansIterations = 100;

    protected override string ProcessId => HandledProcessId;

    protected override List<IFeature> Apply(
        FeatureCollection source,
        StepInputReader inputs,
        CancellationToken cancellationToken)
    {
        var algorithm = ReadAlgorithm(inputs);
        var points = MaterializePoints(source);

        int[] labels;
        switch (algorithm)
        {
            case ClusterAlgorithm.Dbscan:
                labels = RunDbscan(points, ReadEps(inputs), ReadMinPoints(inputs), cancellationToken);
                break;
            case ClusterAlgorithm.KMeans:
                labels = RunKMeans(points, ReadK(inputs), cancellationToken);
                break;
            default:
                throw new TransformInputException(
                    $"algorithm '{algorithm}' is not supported (allowed: dbscan, kmeans)");
        }

        var output = new List<IFeature>(points.Count);
        for (var i = 0; i < points.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Add(WithClusterId(points[i].SourceFeature, labels[i]));
        }

        return output;
    }

    private static Feature WithClusterId(IFeature source, int clusterId)
    {
        var merged = new AttributesTable();
        if (source.Attributes is not null)
        {
            foreach (var name in source.Attributes.GetNames())
            {
                if (string.Equals(name, ClusterIdAttribute, StringComparison.Ordinal))
                {
                    continue;
                }

                merged.Add(name, source.Attributes.GetOptionalValue(name));
            }
        }

        merged.Add(ClusterIdAttribute, clusterId);
        return new Feature(source.Geometry, merged);
    }

    private static List<MaterializedPoint> MaterializePoints(FeatureCollection source)
    {
        var materialized = new List<MaterializedPoint>(source.Count);
        foreach (var feature in source)
        {
            var geometry = feature.Geometry;
            if (geometry is null || geometry.IsEmpty)
            {
                continue;
            }

            var coord = geometry is Point p ? p.Coordinate : geometry.Centroid.Coordinate;
            if (coord is null || double.IsNaN(coord.X) || double.IsNaN(coord.Y))
            {
                continue;
            }

            materialized.Add(new MaterializedPoint(feature, coord.X, coord.Y));
        }

        return materialized;
    }

    /// <summary>
    /// Classic DBSCAN over an in-memory <see cref="STRtree{T}"/> neighbor index.
    /// Returns one label per input point: cluster ids start at 0; noise points
    /// get <see cref="NoiseClusterId"/>. Density-reachability is expanded
    /// breadth-first from each seed core point.
    ///
    /// Candidates are labeled at ENQUEUE time so each point is queued at most
    /// once per cluster — without this, a dense neighborhood can re-enqueue
    /// the same Unassigned point from every overlapping core point's neighbor
    /// scan and queue storage / dequeue work grow quadratically on exactly the
    /// high-density inputs DBSCAN is meant to handle. Noise-then-border reclaim
    /// is preserved: a candidate already labeled <see cref="NoiseClusterId"/>
    /// is re-labeled to the current cluster but NOT re-queued, because a noise
    /// point has fewer than minPoints neighbors by definition and so cannot be
    /// a core point of any cluster.
    /// </summary>
    private static int[] RunDbscan(
        List<MaterializedPoint> points,
        double eps,
        int minPoints,
        CancellationToken cancellationToken)
    {
        var labels = new int[points.Count];
        Array.Fill(labels, Unassigned);

        if (points.Count == 0)
        {
            return labels;
        }

        var index = new STRtree<int>();
        for (var i = 0; i < points.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index.Insert(EnvelopeAround(points[i], eps), i);
        }

        index.Build();

        var queue = new Queue<int>();
        var nextClusterId = 0;
        for (var i = 0; i < points.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (labels[i] != Unassigned)
            {
                continue;
            }

            var seed = QueryNeighbors(index, points, i, eps);
            if (seed.Count < minPoints)
            {
                labels[i] = NoiseClusterId;
                continue;
            }

            var clusterId = nextClusterId++;
            labels[i] = clusterId;
            queue.Clear();

            // Seed expansion: skip the core point itself (already labeled above).
            AssignAndEnqueueNewMembers(queue, labels, seed, clusterId, skipIndex: i);

            while (queue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = queue.Dequeue();
                // labels[current] was assigned to clusterId at enqueue time; we
                // are here purely to expand its neighborhood if it is a core
                // point.
                var neighbors = QueryNeighbors(index, points, current, eps);
                if (neighbors.Count >= minPoints)
                {
                    AssignAndEnqueueNewMembers(queue, labels, neighbors, clusterId, skipIndex: -1);
                }
            }
        }

        return labels;
    }

    /// <summary>
    /// Walks the candidate neighbor list and either (a) labels + enqueues each
    /// Unassigned candidate exactly once or (b) relabels each NoiseClusterId
    /// candidate into the current cluster as a border-point reclaim WITHOUT
    /// enqueueing it (noise points have &lt; minPoints neighbors by definition
    /// and so cannot be core). Candidates already assigned to any cluster
    /// (including this one) are skipped — this is the dedup guard that keeps
    /// dense neighborhoods from re-queueing the same point repeatedly.
    /// </summary>
    private static void AssignAndEnqueueNewMembers(
        Queue<int> queue,
        int[] labels,
        List<int> candidates,
        int clusterId,
        int skipIndex)
    {
        foreach (var candidate in candidates)
        {
            if (candidate == skipIndex)
            {
                continue;
            }

            var label = labels[candidate];
            if (label == Unassigned)
            {
                labels[candidate] = clusterId;
                queue.Enqueue(candidate);
            }
            else if (label == NoiseClusterId)
            {
                // Border-point reclaim: in this cluster but not a core seed.
                labels[candidate] = clusterId;
            }
        }
    }

    /// <summary>
    /// Lloyd's K-Means iterating to a fixed point or
    /// <see cref="MaxKMeansIterations"/>. Initial centroids are seeded
    /// deterministically by stepping through the input at evenly spaced indices
    /// so reruns over the same input produce identical labels.
    /// </summary>
    private static int[] RunKMeans(
        List<MaterializedPoint> points,
        int k,
        CancellationToken cancellationToken)
    {
        var labels = new int[points.Count];
        if (points.Count == 0 || k == 0)
        {
            return labels;
        }

        if (k >= points.Count)
        {
            // More clusters than points: assign each point to its own cluster.
            for (var i = 0; i < points.Count; i++)
            {
                labels[i] = i;
            }

            return labels;
        }

        var centroids = new (double X, double Y)[k];
        var step = (double)points.Count / k;
        for (var c = 0; c < k; c++)
        {
            var index = Math.Min(points.Count - 1, (int)Math.Floor(step * c));
            centroids[c] = (points[index].X, points[index].Y);
        }

        for (var iteration = 0; iteration < MaxKMeansIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var changed = false;
            for (var i = 0; i < points.Count; i++)
            {
                var bestCluster = 0;
                var bestDistance = double.PositiveInfinity;
                for (var c = 0; c < k; c++)
                {
                    var dx = points[i].X - centroids[c].X;
                    var dy = points[i].Y - centroids[c].Y;
                    var distance = (dx * dx) + (dy * dy);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestCluster = c;
                    }
                }

                if (labels[i] != bestCluster)
                {
                    labels[i] = bestCluster;
                    changed = true;
                }
            }

            if (!changed && iteration > 0)
            {
                break;
            }

            var sumX = new double[k];
            var sumY = new double[k];
            var counts = new int[k];
            for (var i = 0; i < points.Count; i++)
            {
                sumX[labels[i]] += points[i].X;
                sumY[labels[i]] += points[i].Y;
                counts[labels[i]]++;
            }

            for (var c = 0; c < k; c++)
            {
                if (counts[c] > 0)
                {
                    centroids[c] = (sumX[c] / counts[c], sumY[c] / counts[c]);
                }
            }
        }

        return labels;
    }

    private static List<int> QueryNeighbors(
        STRtree<int> index,
        List<MaterializedPoint> points,
        int origin,
        double eps)
    {
        var envelope = EnvelopeAround(points[origin], eps);
        var candidates = index.Query(envelope);
        var neighbors = new List<int>(candidates.Count);
        var epsSquared = eps * eps;
        foreach (var candidate in candidates)
        {
            var dx = points[candidate].X - points[origin].X;
            var dy = points[candidate].Y - points[origin].Y;
            if ((dx * dx) + (dy * dy) <= epsSquared)
            {
                neighbors.Add(candidate);
            }
        }

        return neighbors;
    }

    private static Envelope EnvelopeAround(MaterializedPoint point, double eps)
        => new(point.X - eps, point.X + eps, point.Y - eps, point.Y + eps);

    private static ClusterAlgorithm ReadAlgorithm(StepInputReader inputs)
    {
        var raw = inputs.GetOrDefault("algorithm", "dbscan");
        return raw.Trim().ToLowerInvariant() switch
        {
            "dbscan" => ClusterAlgorithm.Dbscan,
            "kmeans" or "k-means" => ClusterAlgorithm.KMeans,
            _ => throw new TransformInputException(
                $"algorithm '{raw}' is not supported (allowed: dbscan, kmeans)"),
        };
    }

    private static double ReadEps(StepInputReader inputs)
    {
        if (!inputs.TryGet("eps", out var raw) || string.IsNullOrWhiteSpace(raw)
            || !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value)
            || value <= 0)
        {
            throw new TransformInputException("'eps' must be a finite positive number for DBSCAN");
        }

        return value;
    }

    private static int ReadMinPoints(StepInputReader inputs)
    {
        if (!inputs.TryGet("minPoints", out var raw) || string.IsNullOrWhiteSpace(raw)
            || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value < 1)
        {
            throw new TransformInputException("'minPoints' must be an integer >= 1 for DBSCAN");
        }

        return value;
    }

    private static int ReadK(StepInputReader inputs)
    {
        if (!inputs.TryGet("k", out var raw) || string.IsNullOrWhiteSpace(raw)
            || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value < 1)
        {
            throw new TransformInputException("'k' must be an integer >= 1 for K-Means");
        }

        return value;
    }

    private const int Unassigned = int.MinValue;

    private enum ClusterAlgorithm
    {
        Dbscan,
        KMeans,
    }

    private readonly record struct MaterializedPoint(IFeature SourceFeature, double X, double Y);
}
