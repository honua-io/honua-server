// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Honua.Geoprocessing.Execution;

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

    // Candidates are labeled when they are enqueued so a dense neighborhood cannot queue the same point once per overlapping core.
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
        foreach (var candidate in candidates)
        {
            var dx = points[candidate].X - points[origin].X;
            var dy = points[candidate].Y - points[origin].Y;
            if (ManagedDistance.IsWithin(dx, dy, eps))
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
