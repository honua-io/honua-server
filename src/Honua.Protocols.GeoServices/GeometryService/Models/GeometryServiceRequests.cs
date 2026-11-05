// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices.GeometryService.Models;

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

internal sealed class SimplifyParameters
{
    public required string[] GeometryJsonStrings { get; init; }
    public string? GeometryType { get; init; }
    public int SR { get; init; }
}

internal sealed class ProjectParameters
{
    public required string[] GeometryJsonStrings { get; init; }
    public string? GeometryType { get; init; }
    public int InSR { get; init; }
    public int OutSR { get; init; }

    public Core.Features.Infrastructure.Crs.DatumTransformationSelection? DatumTransformation { get; init; }
}

internal sealed class BinaryGeometryOperationParameters
{
    public required string[] GeometryJsonStrings { get; init; }
    public string? GeometryType { get; init; }
    public required string OperatorGeometryJson { get; init; }
    public int SR { get; init; }
}

internal sealed class UnionParameters
{
    public required string[] GeometryJsonStrings { get; init; }
    public string? GeometryType { get; init; }
    public int SR { get; init; }
}

internal sealed class DistanceParameters
{
    public required string Geometry1Json { get; init; }
    public required string Geometry2Json { get; init; }
    public int SR { get; init; }
    public string? DistanceUnit { get; init; }
    public bool Geodesic { get; init; }
}

internal sealed class RelationParameters
{
    public required string[] Geometries1Json { get; init; }
    public required string[] Geometries2Json { get; init; }
    public int SR { get; init; }
    public required string Relation { get; init; }
    public string? RelationParam { get; init; }
}

internal sealed class DensifyParameters
{
    public required string[] GeometryJsonStrings { get; init; }
    public string? GeometryType { get; init; }
    public int SR { get; init; }

    public double MaxSegmentLength { get; init; }

    public bool Geodesic { get; init; }
}

internal sealed class ConvexHullParameters
{
    public required string[] GeometryJsonStrings { get; init; }
    public string? GeometryType { get; init; }
    public int SR { get; init; }
}

internal sealed class GeneralizeParameters
{
    public required string[] GeometryJsonStrings { get; init; }
    public string? GeometryType { get; init; }
    public int SR { get; init; }
    public double MaxDeviation { get; init; }
}

internal sealed class LabelPointsParameters
{
    public required string[] GeometryJsonStrings { get; init; }
    public int SR { get; init; }
}

internal sealed class CutParameters
{
    public required string[] TargetJsonStrings { get; init; }
    public string? GeometryType { get; init; }
    public required string CutterJson { get; init; }
    public int SR { get; init; }
}

internal sealed class TrimExtendParameters
{
    public required string[] PolylineJsonStrings { get; init; }
    public required string TrimExtendToJson { get; init; }
    public int SR { get; init; }

    public string? ExtendHow { get; init; }
}

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

internal sealed class AutoCompleteParameters
{
    public required string[] PolygonJsonStrings { get; init; }
    public required string[] PolylineJsonStrings { get; init; }
    public int SR { get; init; }
}

internal sealed class ReshapeParameters
{
    public required string TargetJson { get; init; }
    public required string ReshaperJson { get; init; }
    public int SR { get; init; }
}

internal sealed class FindTransformationsParameters
{
    public int InSR { get; init; }
    public int OutSR { get; init; }
    public int NumOfResults { get; init; } = 1;
}

internal sealed class ToGeoCoordinateStringParameters
{
    public required double[][] Coordinates { get; init; }
    public int SR { get; init; }
    public required string ConversionType { get; init; }
    public string? ConversionMode { get; init; }
    public int NumOfDigits { get; init; } = 5;
    public bool AddSpaces { get; init; } = true;
    public bool Rounding { get; init; } = true;
    public bool OldStyle { get; init; }
    public bool ZoneOneAt180 { get; init; }
}

internal sealed class FromGeoCoordinateStringParameters
{
    public required string[] Strings { get; init; }
    public int SR { get; init; }
    public required string ConversionType { get; init; }
    public string? ConversionMode { get; init; }
    public bool OldStyle { get; init; }
}

internal enum MeasurementCalculationType
{
    Planar,

    Geodesic,

    PreserveShape,

    Ground
}

internal sealed class MeasurementParameters
{
    public required string[] GeometryJsonStrings { get; init; }
    public int SR { get; init; }
    public string? AreaUnit { get; init; }
    public string? LengthUnit { get; init; }
    public MeasurementCalculationType CalculationType { get; init; }
}
