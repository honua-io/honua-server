// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Features.FeatureStore.Domain;

public readonly record struct FeatureExtent
{
    public required double MinX { get; init; }

    public required double MinY { get; init; }

    public required double MaxX { get; init; }

    public required double MaxY { get; init; }

    public required int SpatialReference { get; init; }

    public static FeatureExtent Create(double minX, double minY, double maxX, double maxY, int spatialReference)
        => new()
        {
            MinX = minX,
            MinY = minY,
            MaxX = maxX,
            MaxY = maxY,
            SpatialReference = spatialReference
        };

    public static FeatureExtent FromBoundingBox(BoundingBox boundingBox)
        => new()
        {
            MinX = boundingBox.MinX,
            MinY = boundingBox.MinY,
            MaxX = boundingBox.MaxX,
            MaxY = boundingBox.MaxY,
            SpatialReference = boundingBox.SpatialReferenceId ?? 4326 // Default to WGS84
        };

    public readonly BoundingBox ToBoundingBox()
        => BoundingBox.Create(MinX, MinY, MaxX, MaxY, SpatialReference);

    public readonly double Width => MaxX - MinX;

    public readonly double Height => MaxY - MinY;

    public readonly double CenterX => (MinX + MaxX) / 2.0;

    public readonly double CenterY => (MinY + MaxY) / 2.0;
}
