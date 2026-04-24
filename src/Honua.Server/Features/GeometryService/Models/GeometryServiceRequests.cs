// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.GeometryService.Models;

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
