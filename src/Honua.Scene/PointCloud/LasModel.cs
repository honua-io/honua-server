// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Scene.PointCloud;

/// <summary>
/// Decoded ASPRS LAS public header block fields needed by the point-cloud
/// ingest pipeline (#1201).
/// </summary>
public sealed record LasHeader
{
    /// <summary>LAS major version (1).</summary>
    public required byte VersionMajor { get; init; }

    /// <summary>LAS minor version (1-4).</summary>
    public required byte VersionMinor { get; init; }

    /// <summary>Point data record format (0-10; only supported formats reach callers).</summary>
    public required byte PointDataRecordFormat { get; init; }

    /// <summary>Length in bytes of a single point record.</summary>
    public required ushort PointRecordLength { get; init; }

    /// <summary>Byte offset from the start of the file to the first point record.</summary>
    public required uint OffsetToPointData { get; init; }

    /// <summary>Total number of point records.</summary>
    public required ulong PointCount { get; init; }

    /// <summary>Declared public header size in bytes.</summary>
    public required ushort HeaderSize { get; init; }

    /// <summary>X scale factor applied to the stored integer X.</summary>
    public required double ScaleX { get; init; }

    /// <summary>Y scale factor applied to the stored integer Y.</summary>
    public required double ScaleY { get; init; }

    /// <summary>Z scale factor applied to the stored integer Z.</summary>
    public required double ScaleZ { get; init; }

    /// <summary>X offset added after scaling.</summary>
    public required double OffsetXValue { get; init; }

    /// <summary>Y offset added after scaling.</summary>
    public required double OffsetYValue { get; init; }

    /// <summary>Z offset added after scaling.</summary>
    public required double OffsetZValue { get; init; }

    /// <summary>Minimum descaled X across the cloud.</summary>
    public required double MinX { get; init; }

    /// <summary>Minimum descaled Y across the cloud.</summary>
    public required double MinY { get; init; }

    /// <summary>Minimum descaled Z across the cloud.</summary>
    public required double MinZ { get; init; }

    /// <summary>Maximum descaled X across the cloud.</summary>
    public required double MaxX { get; init; }

    /// <summary>Maximum descaled Y across the cloud.</summary>
    public required double MaxY { get; init; }

    /// <summary>Maximum descaled Z across the cloud.</summary>
    public required double MaxZ { get; init; }

    /// <summary>True when the point format carries per-point RGB.</summary>
    public bool HasColor => PointDataRecordFormat is 2 or 3 or 7 or 8;
}

/// <summary>
/// One decoded LAS point: descaled position plus the preserved attributes the
/// ingest pipeline carries into the output tileset (#1201).
/// </summary>
/// <param name="X">Descaled X in the file's horizontal units.</param>
/// <param name="Y">Descaled Y in the file's horizontal units.</param>
/// <param name="Z">Descaled Z (elevation) in meters.</param>
/// <param name="Intensity">Pulse return intensity (0-65535).</param>
/// <param name="Classification">ASPRS classification code.</param>
/// <param name="HasColor">True when <paramref name="Red"/>/<paramref name="Green"/>/<paramref name="Blue"/> are meaningful.</param>
/// <param name="Red">16-bit red channel (0 when the format has no colour).</param>
/// <param name="Green">16-bit green channel.</param>
/// <param name="Blue">16-bit blue channel.</param>
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
