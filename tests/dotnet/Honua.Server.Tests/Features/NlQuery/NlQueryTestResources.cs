// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Tests.Features.NlQuery;

internal static class NlQueryTestResources
{
    internal static MetadataV2Resource CriticalFacilities { get; } = CreateResource(
        "critical_facilities",
        MetadataV2GeometryType.Point,
        [
            Field("facility_type", MetadataV2FieldType.String, length: 50),
            Field("status", MetadataV2FieldType.String, length: 20),
            Field("capacity", MetadataV2FieldType.Integer),
            Field("shape", MetadataV2FieldType.Geometry, roles: ["geometry.primary"]),
        ],
        description: "Fixture critical facilities layer");

    internal static MetadataV2Resource TestParks { get; } = CreateResource(
        "test_parks",
        MetadataV2GeometryType.Point,
        [
            Field("objectid", MetadataV2FieldType.Integer, nullable: false, roles: ["id.primary"]),
            Field("name", MetadataV2FieldType.String, length: 100),
            Field("population", MetadataV2FieldType.Integer),
            Field("shape", MetadataV2FieldType.Geometry, roles: ["geometry.primary"]),
        ],
        description: "Parks layer");

    private static MetadataV2Resource CreateResource(
        string name,
        MetadataV2GeometryType geometryType,
        IReadOnlyList<MetadataV2Field> fields,
        string? description = null)
    {
        var geometryField = fields.FirstOrDefault(field =>
            field.Type is MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography);

        return new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = $"resource:{name}",
                Name = name,
                Description = description,
            },
            SchemaFields = fields,
            Spatial = new MetadataV2ResourceSpatial
            {
                SpatialReference = MetadataV2SpatialReference.Wgs84,
                GeometryType = geometryType,
                PrimaryGeometryField = geometryField?.Name,
            },
        };
    }

    private static MetadataV2Field Field(
        string name,
        MetadataV2FieldType type,
        bool nullable = true,
        int? length = null,
        IReadOnlyList<string>? roles = null)
        => new()
        {
            Name = name,
            Type = type,
            Nullable = nullable,
            Length = length,
            SemanticRoles = roles ?? [],
        };
}
