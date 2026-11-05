// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Scene.Domain;

public enum SceneGeometryKind
{
    Point,

    LineString,

    Polygon
}

public readonly record struct SceneVertex(double Longitude, double Latitude, double? Height);

public sealed record SceneFeatureGeometry
{
    public required SceneGeometryKind Kind { get; init; }

    public required IReadOnlyList<SceneVertex> Vertices { get; init; }
}

public sealed record SceneFeature
{
    public required long Id { get; init; }

    public required SceneFeatureGeometry Geometry { get; init; }

    public IReadOnlyDictionary<string, object?> Attributes { get; init; }
        = new Dictionary<string, object?>(StringComparer.Ordinal);
}
