// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

internal static class FormatterTestResources
{
    private const string GeometryFieldName = "shape";

    public static MetadataV2Object WithGeometryType(
        MetadataV2ObjectMetadata metadata,
        MetadataV2GeometryType geometryType,
        params MetadataV2Field[] fields)
        => new()
        {
            Metadata = metadata,
            SchemaFields = [.. fields, GeometryField(["geometry.primary"])],
            Spatial = new MetadataV2ObjectSpatial
            {
                SpatialReference = MetadataV2SpatialReference.Wgs84,
                GeometryType = geometryType,
                PrimaryGeometryField = GeometryFieldName
            }
        };

    // Geometry is declared only by a Geometry-typed schema field; the Spatial slot stays unset.
    public static MetadataV2Object WithoutSpatialSlot(MetadataV2ObjectMetadata metadata, params MetadataV2Field[] fields)
        => new()
        {
            Metadata = metadata,
            SchemaFields = [.. fields, GeometryField(semanticRoles: null)]
        };

    public static byte[] CreatePointWkb(double x, double y)
        => new WKBWriter().Write(new Point(x, y) { SRID = 4326 });

    public static byte[] CreatePointWkbWithZm(double x, double y, double z, double m)
        => new WKBWriter(ByteOrder.LittleEndian, handleSRID: false, emitZ: true, emitM: true)
            .Write(new Point(new CoordinateZM(x, y, z, m)) { SRID = 4326 });

    private static MetadataV2Field GeometryField(string[]? semanticRoles)
    {
        var field = new MetadataV2Field
        {
            Name = GeometryFieldName,
            Type = MetadataV2FieldType.Geometry,
            Nullable = true
        };
        return semanticRoles is null ? field : field with { SemanticRoles = semanticRoles };
    }
}
