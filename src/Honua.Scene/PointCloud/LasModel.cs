// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Scene.PointCloud;

public sealed record LasHeader
{
    public required byte VersionMajor { get; init; }

    public required byte VersionMinor { get; init; }

    public required byte PointDataRecordFormat { get; init; }

    public required ushort PointRecordLength { get; init; }

    public required uint OffsetToPointData { get; init; }

    public required ulong PointCount { get; init; }

    public required ushort HeaderSize { get; init; }

    public required double ScaleX { get; init; }

    public required double ScaleY { get; init; }

    public required double ScaleZ { get; init; }

    public required double OffsetXValue { get; init; }

    public required double OffsetYValue { get; init; }

    public required double OffsetZValue { get; init; }

    public required double MinX { get; init; }

    public required double MinY { get; init; }

    public required double MinZ { get; init; }

    public required double MaxX { get; init; }

    public required double MaxY { get; init; }

    public required double MaxZ { get; init; }

    public bool HasColor => PointDataRecordFormat is 2 or 3 or 7 or 8;
}

public readonly record struct LasPoint(
    double X,
    double Y,
    double Z,
    ushort Intensity,
    byte Classification,
    bool HasColor,
    ushort Red,
    ushort Green,
    ushort Blue);
