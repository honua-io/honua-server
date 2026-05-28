// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.FileImport.Services.FileGdb;
namespace Honua.Core.Features.FileImport.Services.FileGdb;

/// <summary>
/// Represents a field definition in a FileGDB table.
/// </summary>
internal sealed class GdbField
{
    /// <summary>
    /// Gets the field name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the field alias (display name). Empty string if not specified.
    /// </summary>
    public required string Alias { get; init; }

    /// <summary>
    /// Gets the field data type.
    /// </summary>
    public required GdbFieldType FieldType { get; init; }

    /// <summary>
    /// Gets whether this field accepts null values.
    /// </summary>
    public bool IsNullable { get; init; }

    /// <summary>
    /// Gets the maximum length for string fields.
    /// </summary>
    public int MaxLength { get; init; }

    /// <summary>
    /// Gets the geometry definition for geometry fields.
    /// </summary>
    public GdbGeometryDef? GeometryDef { get; init; }
}

/// <summary>
/// Defines the spatial reference and coordinate encoding parameters for a geometry field.
/// </summary>
internal sealed class GdbGeometryDef
{
    /// <summary>
    /// Gets the WKT spatial reference string.
    /// </summary>
    public string? WktSpatialReference { get; init; }

    /// <summary>
    /// Gets the EPSG SRID, if detected from the WKT.
    /// </summary>
    public int Srid { get; init; }

    /// <summary>
    /// Gets the X origin for coordinate encoding.
    /// </summary>
    public double XOrigin { get; init; }

    /// <summary>
    /// Gets the Y origin for coordinate encoding.
    /// </summary>
    public double YOrigin { get; init; }

    /// <summary>
    /// Gets the XY scale factor for coordinate encoding.
    /// </summary>
    public double XYScale { get; init; }

    /// <summary>
    /// Gets the Z origin for coordinate encoding.
    /// </summary>
    public double ZOrigin { get; init; }

    /// <summary>
    /// Gets the Z scale factor for coordinate encoding.
    /// </summary>
    public double ZScale { get; init; }

    /// <summary>
    /// Gets the M origin for coordinate encoding.
    /// </summary>
    public double MOrigin { get; init; }

    /// <summary>
    /// Gets the M scale factor for coordinate encoding.
    /// </summary>
    public double MScale { get; init; }

    /// <summary>
    /// Gets the XY tolerance.
    /// </summary>
    public double XYTolerance { get; init; }

    /// <summary>
    /// Gets whether the geometry supports Z coordinates.
    /// </summary>
    public bool HasZ { get; init; }

    /// <summary>
    /// Gets whether the geometry supports M (measure) values.
    /// </summary>
    public bool HasM { get; init; }
}
