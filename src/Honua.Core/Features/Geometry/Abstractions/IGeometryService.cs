// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Geometry.Abstractions;

public interface IGeometryService
{
    (bool HasZ, bool HasM) DetectZM(byte[]? wkb);

    (bool HasZ, bool HasM) DetectZM(Memory<byte> wkb);

    string? ConvertWkbToGeoJson(byte[]? wkb);

    string? ConvertWkbToGeoJson(Memory<byte> wkb);

    byte[]? ConvertGeoJsonToWkb(string? geoJson, int? srid = null);

    byte[]? ConvertGeoJsonToWkb(string? geoJson, int? srid, bool allowContainers)
    {
        if (allowContainers)
        {
            throw new NotSupportedException(
                "GeoJSON container conversion is not supported by this geometry service implementation.");
        }

        return ConvertGeoJsonToWkb(geoJson, srid);
    }

    byte[]? ConvertWktToWkb(string? wkt, int? srid = null);

    GeometryInfo? GetGeometryInfo(byte[]? wkb);
}

public sealed record GeometryInfo
{
    public required string GeometryType { get; init; }

    public int VertexCount { get; init; }

    public int RingCount { get; init; }

    public long WkbSize { get; init; }

    public bool HasZ { get; init; }

    public bool HasM { get; init; }

    public int? Srid { get; init; }
}
