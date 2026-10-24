// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Io.Export;

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
