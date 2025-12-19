// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Catalog.Domain;

/// <summary>
/// Field data types supported in layer schemas
/// </summary>
/// <remarks>
/// CA1720 suppressed: Enum values match GIS industry standard field type names
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name")]
public enum FieldType
{
    /// <summary>
    /// Text string (VARCHAR, TEXT)
    /// </summary>
    String,

    /// <summary>
    /// 32-bit signed integer
    /// </summary>
    Integer,

    /// <summary>
    /// 64-bit signed integer (BIGINT)
    /// </summary>
    BigInteger,

    /// <summary>
    /// Double-precision floating point
    /// </summary>
    Double,

    /// <summary>
    /// Single-precision floating point
    /// </summary>
    Float,

    /// <summary>
    /// Boolean true/false
    /// </summary>
    Boolean,

    /// <summary>
    /// Date and time with timezone
    /// </summary>
    DateTime,

    /// <summary>
    /// Date only (no time component)
    /// </summary>
    Date,

    /// <summary>
    /// Time only (no date component)
    /// </summary>
    Time,

    /// <summary>
    /// Geometry field (PostGIS geometry type)
    /// </summary>
    Geometry,

    /// <summary>
    /// JSON/JSONB field
    /// </summary>
    Json,

    /// <summary>
    /// Binary large object
    /// </summary>
    Binary,

    /// <summary>
    /// Unique identifier (UUID/GUID)
    /// </summary>
    Uuid
}
