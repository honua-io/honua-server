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
