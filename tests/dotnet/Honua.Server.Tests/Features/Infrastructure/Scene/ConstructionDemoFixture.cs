// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Scene.Domain;
using Honua.Core.Features.Shared.Models;

namespace Honua.Server.Tests.Features.Infrastructure.Scene;

/// <summary>
/// Static, deterministic fixture for the NVIDIA construction demo scene
/// (#899). Provides the layer definition, polygon footprints, and matching
/// extrusion configuration that exercise the v1 generation pipeline against
/// a representative AEC dataset without requiring Postgres.
/// </summary>
/// <remarks>
/// <para>
/// Coordinates fall inside the prebuilt demo bounds documented in
/// <c>tests/fixtures/scenes/nvidia-construction/tileset.json</c>:
/// west = -121.98°, south = 37.37°, east = -121.96°, north = 37.38°. Every
/// value is a compile-time constant so the byte-hash of generated output
/// remains stable across environments.
/// </para>
/// <para>
/// Five footprints span the three demo work packages (foundation / structure
/// / envelope) with heights from 8m to 80m, matching the prebuilt fixture's
/// <c>extras.bounds.maxHeight</c>.
/// </para>
/// </remarks>
internal static class ConstructionDemoFixture
{
    /// <summary>Stable layer id reserved for the construction demo seed entry.</summary>
    public const int LayerId = 9;

    /// <summary>Layer name surfaced in the catalog and admin UI.</summary>
    public const string LayerName = "NVIDIA Construction Site";

    /// <summary>Stable scene id used by the prebuilt and generated tilesets.</summary>
    public const string SceneId = "nvidia-construction";

    /// <summary>Western bound (degrees) — matches the prebuilt fixture.</summary>
    public const double WestLongitude = -121.98;

    /// <summary>Southern bound (degrees) — matches the prebuilt fixture.</summary>
    public const double SouthLatitude = 37.37;

    /// <summary>Eastern bound (degrees) — matches the prebuilt fixture.</summary>
    public const double EastLongitude = -121.96;

    /// <summary>Northern bound (degrees) — matches the prebuilt fixture.</summary>
    public const double NorthLatitude = 37.38;

    /// <summary>Maximum extrusion height (meters) — matches the prebuilt bounds.</summary>
    public const double MaxExtrusionHeightMeters = 80.0;

    /// <summary>Field name driving extrusion height.</summary>
    public const string HeightField = "height_m";

    /// <summary>Metadata v2 extrusion configuration matching the layer's <c>height_m</c> field.</summary>
    public static MetadataV2ExtrusionInfo Extrusion { get; } = new()
    {
        HeightField = HeightField,
        Unit = MetadataV2VerticalUnits.Meters,
        DefaultHeight = 10.0
    };

    /// <summary>Metadata v2 graph carrying the demo resource extrusion metadata.</summary>
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
                new MetadataV2Resource
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = resourceId, Name = LayerName },
                    Type = MetadataV2ResourceType.FeatureDataset,
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
                    Spatial = new MetadataV2ResourceSpatial
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

    /// <summary>Five deterministic polygon footprints covering all three work packages.</summary>
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
