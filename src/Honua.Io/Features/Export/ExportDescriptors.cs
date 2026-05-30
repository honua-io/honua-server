// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Export;

internal sealed record ExportField(string Name, ExportFieldType Type, bool Nullable);

[JsonConverter(typeof(JsonStringEnumConverter<ExportFieldType>))]
internal enum ExportFieldType
{
    Unknown,
    String,
    Integer,
    BigInteger,
    Double,
    Float,
    Boolean,
    DateTime,
    Date,
    Time,
    Json,
    Binary,
    Uuid
}

[JsonConverter(typeof(JsonStringEnumConverter<ExportGeometryType>))]
internal enum ExportGeometryType
{
    None,
    Point,
    MultiPoint,
    LineString,
    MultiLineString,
    Polygon,
    MultiPolygon,
    GeometryCollection,
    Mixed
}
