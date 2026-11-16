// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Scene.Domain;
using Honua.Core.Features.Shared.Models;

namespace Honua.Server.Tests.Features.Infrastructure.Scene;

internal static class ConstructionDemoFixture
{
    public const int LayerId = 9;

    public const string LayerName = "NVIDIA Construction Site";

    public const string SceneId = "nvidia-construction";

    public const double WestLongitude = -121.98;

    public const double SouthLatitude = 37.37;

    public const double EastLongitude = -121.96;

    public const double NorthLatitude = 37.38;

    public const double MaxExtrusionHeightMeters = 80.0;

    public const string HeightField = "height_m";

    public static MetadataV2ExtrusionInfo Extrusion { get; } = new()
    {
        HeightField = HeightField,
        Unit = MetadataV2VerticalUnits.Meters,
        DefaultHeight = 10.0
    };

    public static MetadataV2Graph BuildMetadataGraph(bool includeExtrusion = true)
    {
        const string resourceId = "res-construction-demo";
        const string bindingId = "binding-construction-demo";

        return new MetadataV2Graph
        {
            Revision = 1,
            Environment = "test",
            GeneratedAt = DateTimeOffset.UtcNow,
            Resources =
            [
                new MetadataV2Object
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = resourceId, Name = LayerName },
                    Type = MetadataV2ObjectType.FeatureDataset,
                    StorageBindingIds = [bindingId],
                    SchemaFields =
                    [
                        new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false },
                        new MetadataV2Field { Name = "shape", Type = MetadataV2FieldType.Geometry, Nullable = false },
                        new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Nullable = false, Length = 64 },
                        new MetadataV2Field { Name = "height_m", Type = MetadataV2FieldType.Double, Nullable = false },
                        new MetadataV2Field { Name = "phase", Type = MetadataV2FieldType.String, Nullable = false, Length = 32 },
                        new MetadataV2Field { Name = "work_package_id", Type = MetadataV2FieldType.String, Nullable = false, Length = 32 }
                    ],
                    Spatial = new MetadataV2ObjectSpatial
                    {
                        SpatialReference = MetadataV2SpatialReference.Wgs84,
                        GeometryType = MetadataV2GeometryType.Polygon,
                        PrimaryGeometryField = "shape"
                    },
                    Extrusion = includeExtrusion ? Extrusion : null
                }
            ],
            StorageBindings =
            [
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = bindingId, Name = bindingId },
                    ResourceId = resourceId,
                    StorageType = MetadataV2StorageType.RelationalTable,
                    Locator = $"features:{LayerId}",
                    StorageLayerId = LayerId
                }
            ]
        };
    }

    public static IReadOnlyList<SceneFeature> Features { get; } = BuildFeatures();

    // objectids match the demo seed in tests/seed/server.yaml (PK rows
    // 9001-9005). PostgresSceneFeatureSource projects the row PK into the
    // metadata `objectid` column for any attribute whose field is named
    // `objectid` / `object_id` / `id`, so the in-memory fixture must use the
    // same values to keep the seeded admin-generation path and the hermetic
    // unit fixture observationally identical.
    private static SceneFeature[] BuildFeatures() =>
    [
        BuildFeature(
            objectid: 9001,
            name: "Main Tower — Foundation",
            heightMeters: 8.0,
            phase: "foundation",
            workPackageId: "wp-foundation",
            ring:
            [
                new SceneVertex(-121.9786, 37.3784, 0.0),
                new SceneVertex(-121.9784, 37.3784, 0.0),
                new SceneVertex(-121.9784, 37.3786, 0.0),
                new SceneVertex(-121.9786, 37.3786, 0.0),
                new SceneVertex(-121.9786, 37.3784, 0.0)
            ]),
        BuildFeature(
            objectid: 9002,
            name: "Main Tower — Structural Frame",
            heightMeters: 60.0,
            phase: "structure",
            workPackageId: "wp-structure",
            ring:
            [
                new SceneVertex(-121.9742, 37.3778, 0.0),
                new SceneVertex(-121.9738, 37.3778, 0.0),
                new SceneVertex(-121.9738, 37.3782, 0.0),
                new SceneVertex(-121.9742, 37.3782, 0.0),
                new SceneVertex(-121.9742, 37.3778, 0.0)
            ]),
        BuildFeature(
            objectid: 9003,
            name: "Equipment Yard Slab",
            heightMeters: 8.0,
            phase: "foundation",
            workPackageId: "wp-foundation",
            ring:
            [
                new SceneVertex(-121.9782, 37.3710, 0.0),
                new SceneVertex(-121.9778, 37.3710, 0.0),
                new SceneVertex(-121.9778, 37.3714, 0.0),
                new SceneVertex(-121.9782, 37.3714, 0.0),
                new SceneVertex(-121.9782, 37.3710, 0.0)
            ]),
        BuildFeature(
            objectid: 9004,
            name: "Tower Annex",
            heightMeters: 35.0,
            phase: "structure",
            workPackageId: "wp-structure",
            ring:
            [
                new SceneVertex(-121.9724, 37.3723, 0.0),
                new SceneVertex(-121.9720, 37.3723, 0.0),
                new SceneVertex(-121.9720, 37.3727, 0.0),
                new SceneVertex(-121.9724, 37.3727, 0.0),
                new SceneVertex(-121.9724, 37.3723, 0.0)
            ]),
        BuildFeature(
            objectid: 9005,
            name: "Envelope Shell — Planned",
            heightMeters: 80.0,
            phase: "not_started",
            workPackageId: "wp-envelope",
            ring:
            [
                new SceneVertex(-121.9662, 37.3758, 0.0),
                new SceneVertex(-121.9658, 37.3758, 0.0),
                new SceneVertex(-121.9658, 37.3762, 0.0),
                new SceneVertex(-121.9662, 37.3762, 0.0),
                new SceneVertex(-121.9662, 37.3758, 0.0)
            ])
    ];

    private static SceneFeature BuildFeature(
        int objectid,
        string name,
        double heightMeters,
        string phase,
        string workPackageId,
        SceneVertex[] ring)
    {
        return new SceneFeature
        {
            Id = objectid,
            Geometry = new SceneFeatureGeometry
            {
                Kind = SceneGeometryKind.Polygon,
                Vertices = ring
            },
            Attributes = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["objectid"] = objectid,
                ["name"] = name,
                ["height_m"] = heightMeters,
                ["phase"] = phase,
                ["work_package_id"] = workPackageId
            }
        };
    }
}
