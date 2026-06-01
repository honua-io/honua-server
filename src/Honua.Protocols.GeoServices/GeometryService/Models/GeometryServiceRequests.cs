// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices.GeometryService.Models;

/// <summary>
/// Parsed parameters for the buffer operation.
/// Populated by <see cref="Services.GeometryServiceRequestParser"/>, not by ASP.NET model binding.
/// </summary>
internal sealed class BufferParameters
{
    public required string[] GeometryJsonStrings { get; init; }
    public string? GeometryType { get; init; }
    public int InSR { get; init; }
    public int? OutSR { get; init; }
    public int? BufferSR { get; init; }
    public required double[] Distances { get; init; }
    public string? Unit { get; init; }
    public double DistanceUnitToMetersFactor { get; init; } = 1.0;
    public bool UnionResults { get; init; }
    public bool Geodesic { get; init; }
}

/// <summary>
/// Parsed parameters for the simplify operation (topological correction via ST_MakeValid).
/// </summary>
internal sealed class SimplifyParameters
{
    public required string[] GeometryJsonStrings { get; init; }
    public string? GeometryType { get; init; }
    public int SR { get; init; }
}

/// <summary>
/// Parsed parameters for the project (reproject) operation.
/// </summary>
internal sealed class ProjectParameters
{
    public required string[] GeometryJsonStrings { get; init; }
    public string? GeometryType { get; init; }
    public int InSR { get; init; }
    public int OutSR { get; init; }
}

/// <summary>
/// Parsed parameters for binary geometry operations that apply a single geometry against many geometries.
/// Used by intersect, clip, and difference.
/// </summary>
internal sealed class BinaryGeometryOperationParameters
{
    public required string[] GeometryJsonStrings { get; init; }
    public string? GeometryType { get; init; }
    public required string OperatorGeometryJson { get; init; }
    public int SR { get; init; }
}

/// <summary>
/// Parsed parameters for union operation.
/// </summary>
internal sealed class UnionParameters
{
    public required string[] GeometryJsonStrings { get; init; }
    public string? GeometryType { get; init; }
    public int SR { get; init; }
}

/// <summary>
/// Parsed parameters for the distance operation (closest distance between two geometries).
/// </summary>
internal sealed class DistanceParameters
{
    public required string Geometry1Json { get; init; }
    public required string Geometry2Json { get; init; }
    public int SR { get; init; }
    public string? DistanceUnit { get; init; }
    public bool Geodesic { get; init; }
}

/// <summary>
/// Parsed parameters for the relation operation (pairwise spatial relation testing).
/// </summary>
internal sealed class RelationParameters
{
    public required string[] Geometries1Json { get; init; }
    public required string[] Geometries2Json { get; init; }
    public int SR { get; init; }
    public required string Relation { get; init; }
    public string? RelationParam { get; init; }
}

/// <summary>
/// Parsed parameters for the densify operation (insert vertices along segments).
/// </summary>
internal sealed class DensifyParameters
{
    public required string[] GeometryJsonStrings { get; init; }
    public string? GeometryType { get; init; }
    public int SR { get; init; }
    public double MaxSegmentLength { get; init; }
}

/// <summary>
/// Parsed parameters for the convexHull operation.
/// </summary>
internal sealed class ConvexHullParameters
{
    public required string[] GeometryJsonStrings { get; init; }
    public string? GeometryType { get; init; }
    public int SR { get; init; }
}

/// <summary>
/// Parsed parameters for the generalize operation (Douglas-Peucker simplification).
/// </summary>
internal sealed class GeneralizeParameters
{
    public required string[] GeometryJsonStrings { get; init; }
    public string? GeometryType { get; init; }
    public int SR { get; init; }
    public double MaxDeviation { get; init; }
}

/// <summary>
/// Parsed parameters for the labelPoints operation (interior point of each polygon).
/// </summary>
internal sealed class LabelPointsParameters
{
    public required string[] GeometryJsonStrings { get; init; }
    public int SR { get; init; }
}

/// <summary>
/// Parsed parameters for the cut operation (split target geometries by a cutter polyline).
/// </summary>
internal sealed class CutParameters
{
    public required string[] TargetJsonStrings { get; init; }
    public string? GeometryType { get; init; }
    public required string CutterJson { get; init; }
    public int SR { get; init; }
}

/// <summary>
/// Parsed parameters for the trimExtend operation (trim or extend polylines against a trim polyline).
/// </summary>
internal sealed class TrimExtendParameters
{
    public required string[] PolylineJsonStrings { get; init; }
    public required string TrimExtendToJson { get; init; }
    public int SR { get; init; }
    public string? ExtendHow { get; init; }
}

/// <summary>
/// Parsed parameters for the offset operation (offset polylines/polygons by a distance).
/// </summary>
internal sealed class OffsetParameters
{
    public required string[] GeometryJsonStrings { get; init; }
    public string? GeometryType { get; init; }
    public int SR { get; init; }
    public double OffsetDistance { get; init; }
    public string? OffsetUnit { get; init; }
    public string? OffsetHow { get; init; }
    public double BevelRatio { get; init; }
}

/// <summary>
/// Parsed parameters for the autoComplete operation (close polygons from boundary polylines).
/// </summary>
internal sealed class AutoCompleteParameters
{
    public required string[] PolygonJsonStrings { get; init; }
    public required string[] PolylineJsonStrings { get; init; }
    public int SR { get; init; }
}

/// <summary>
/// Parsed parameters for the reshape operation (reshape a polygon/polyline with a reshaper line).
/// </summary>
internal sealed class ReshapeParameters
{
    public required string TargetJson { get; init; }
    public required string ReshaperJson { get; init; }
    public int SR { get; init; }
}

/// <summary>
/// Parsed parameters for the findTransformations operation (candidate datum transformations).
/// </summary>
internal sealed class FindTransformationsParameters
{
    public int InSR { get; init; }
    public int OutSR { get; init; }
}

/// <summary>
/// Parsed parameters for area and length operations.
/// </summary>
internal enum MeasurementCalculationType
{
    Planar,
    Geodesic,
    PreserveShape
}

/// <summary>
/// Parsed parameters for area and length operations.
/// </summary>
internal sealed class MeasurementParameters
{
    public required string[] GeometryJsonStrings { get; init; }
    public int SR { get; init; }
    public string? AreaUnit { get; init; }
    public string? LengthUnit { get; init; }
    public MeasurementCalculationType CalculationType { get; init; }
}
