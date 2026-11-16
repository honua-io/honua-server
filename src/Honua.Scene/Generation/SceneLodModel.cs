// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Scene.Domain;

namespace Honua.Core.Features.Scene.Generation;

public sealed record SceneLodOptions
{
    public int MaxFeaturesPerTile { get; init; } = 2_000;

    public int MaxDepth { get; init; } = 12;

    public int InteriorSampleCount { get; init; } = 256;

    public static SceneLodOptions SingleTile { get; } = new()
    {
        MaxFeaturesPerTile = int.MaxValue,
        MaxDepth = 0,
        InteriorSampleCount = 0
    };
}

public sealed class SceneTileTree
{
    public SceneTileTree(SceneTileNode root, double rootGeometricError)
    {
        Root = root ?? throw new ArgumentNullException(nameof(root));
        RootGeometricError = rootGeometricError;
    }

    public SceneTileNode Root { get; }

    public double RootGeometricError { get; }

    public int ContentNodeCount => CountContentNodes(Root);

    public int MaxDepthReached => ComputeMaxDepth(Root);

    private static int CountContentNodes(SceneTileNode node)
    {
        var count = node.Features.Count > 0 ? 1 : 0;
        foreach (var child in node.Children)
        {
            count += CountContentNodes(child);
        }
        return count;
    }

    private static int ComputeMaxDepth(SceneTileNode node)
    {
        // Not a candidate for .Where(...): this is a running-max reduction, not a
        // filter — the if-condition reads the very `max` accumulator its own body
        // mutates, so a Where(c => c > max) would be technically equivalent only by
        // relying on LINQ's per-item lazy evaluation ordering, which is more
        // confusing than the plain loop it would replace.
        var max = node.Depth;
        foreach (var childMax in (node.Children.Select(ComputeMaxDepth)).Where(childMax => childMax > max))
        {
            max = childMax;
        }
        return max;
    }
}

public sealed class SceneTileNode
{
    public SceneTileNode(
        double west,
        double south,
        double east,
        double north,
        double geometricError,
        int depth,
        IReadOnlyList<SceneFeature> features,
        IReadOnlyList<SceneTileNode> children)
    {
        West = west;
        South = south;
        East = east;
        North = north;
        GeometricError = geometricError;
        Depth = depth;
        Features = features ?? throw new ArgumentNullException(nameof(features));
        Children = children ?? throw new ArgumentNullException(nameof(children));
    }

    public double West { get; }

    public double South { get; }

    public double East { get; }

    public double North { get; }

    public double GeometricError { get; }

    public int Depth { get; }

    public IReadOnlyList<SceneFeature> Features { get; }

    public IReadOnlyList<SceneTileNode> Children { get; }

    public bool IsLeaf => Children.Count == 0;

    public double[] BoundsDegrees => [West, South, East, North];
}
