// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Scene.PointCloud;

public sealed record CopcInfo
{
    public required byte VersionMajor { get; init; }

    public required byte VersionMinor { get; init; }

    public required byte PointDataRecordFormat { get; init; }

    public required ulong PointCount { get; init; }

    public required ushort HeaderSize { get; init; }

    public required uint OffsetToPointData { get; init; }

    public required double MinX { get; init; }

    public required double MinY { get; init; }

    public required double MinZ { get; init; }

    public required double MaxX { get; init; }

    public required double MaxY { get; init; }

    public required double MaxZ { get; init; }

    public required double CenterX { get; init; }

    public required double CenterY { get; init; }

    public required double CenterZ { get; init; }

    public required double HalfSize { get; init; }

    public required double Spacing { get; init; }

    public required ulong RootHierarchyOffset { get; init; }

    public required ulong RootHierarchySize { get; init; }

    public required double GpsTimeMinimum { get; init; }

    public required double GpsTimeMaximum { get; init; }

    public bool HasColor => PointDataRecordFormat is 7 or 8;
}
