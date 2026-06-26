// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Scene.Conversion;
using Honua.Core.Features.Scene.Domain;
using Honua.Scene;

namespace Honua.Protocols.Scene.I3s;

/// <summary>
/// Builds Esri I3S SceneServer descriptors (service + scene layer) from a
/// registered Honua <see cref="SceneDataset"/> and its known geographic extent.
/// This is the inverse of <see cref="I3sToTilesetConverter"/>: it presents a
/// hosted 3D Tiles scene to ArcGIS / I3S clients as a read-only 3D Object
/// scene layer.
/// </summary>
/// <remarks>
/// <para>
/// This slice serves the service and layer descriptor JSON only: it is a
/// metadata/descriptor preview. Per-node geometry streaming (node pages,
/// geometry/attribute/texture binary resources, the <c>nodes/*</c> store) is a
/// tracked follow-up (#1202) and the server maps no node/geometry routes yet.
/// </para>
/// <para>
/// The descriptor therefore deliberately does NOT advertise a fetchable
/// <c>store.rootNode</c> / node-page resource: doing so would make a conformant
/// I3S/ArcGIS client request a node URL that 404s. It carries only the
/// spec-required scalar fields that are honestly knowable today (id, name,
/// version, spatialReference, heightModelInfo, fullExtent, and store id/profile/
/// version), so the advertised contract matches what the server can actually
/// serve until per-node geometry lands.
/// </para>
/// </remarks>
internal static class I3sSceneServiceBuilder
{
    /// <summary>Default I3S layer type Honua serves for hosted scenes.</summary>
    public const string DefaultLayerType = "3DObject";

    /// <summary>WGS-84 geographic well-known id used for served layers.</summary>
    public const int Wgs84Wkid = 4326;

    /// <summary>
    /// WGS-84 ellipsoidal-height vertical well-known id advertised on the
    /// layer's spatial reference (EPSG:115700, the ellipsoidal vertical CRS for
    /// WGS-84) so a client knows the z values are ellipsoidal metres.
    /// </summary>
    public const int Wgs84EllipsoidVcsWkid = 115700;

    /// <summary>The single layer id Honua exposes per scene (always 0).</summary>
    public const int LayerId = 0;

    /// <summary>
    /// I3S spec version targeted by the served descriptor shape.
    /// </summary>
    public const string I3sVersion = "1.7";

    /// <summary>
    /// Builds the I3S scene-layer descriptor for a scene with the supplied
    /// extent. Returns a 3D Object layer rooted at the geographic extent in
    /// WGS-84.
    /// </summary>
    /// <param name="scene">The hosted scene dataset.</param>
    /// <param name="extent">
    /// The scene's WGS-84 extent, when known. When the extent carries a
    /// vertical range (<see cref="SceneExtent.ZMin"/>/<see cref="SceneExtent.ZMax"/>,
    /// read from the tileset's root bounding volume) the descriptor advertises a
    /// z-bearing <c>fullExtent</c>; otherwise the vertical extent is omitted
    /// rather than fabricated.
    /// </param>
    /// <param name="datasetType">
    /// The dataset's I3S source kind, mapped to the advertised <c>layerType</c>
    /// and store <c>profile</c> (#1812).
    /// </param>
    /// <param name="advertiseNodePages">
    /// When <see langword="true"/>, the store advertises <c>nodePages</c> and a
    /// <c>defaultGeometrySchema</c> so a conformant client traverses the layer
    /// through <c>nodepages/{n}</c> (#1809). Only set when the tileset actually
    /// projects to fetchable node pages.
    /// </param>
    public static I3sSceneLayerDocument BuildLayer(
        SceneDataset scene,
        SceneExtent? extent,
        SceneDatasetType datasetType = SceneDatasetType.HostedTiles,
        bool advertiseNodePages = false)
    {
        ArgumentNullException.ThrowIfNull(scene);

        var spatialReference = new I3sSpatialReference
        {
            Wkid = Wgs84Wkid,
            LatestWkid = Wgs84Wkid,
            VcsWkid = Wgs84EllipsoidVcsWkid,
        };

        I3sFullExtent? fullExtent = extent is null
            ? null
            : new I3sFullExtent
            {
                Xmin = extent.XMin,
                Ymin = extent.YMin,
                Xmax = extent.XMax,
                Ymax = extent.YMax,
                Zmin = extent.ZMin,
                Zmax = extent.ZMax,
                SpatialReference = spatialReference,
            };

        // #1812: advertise the I3S layerType + store profile the dataset's source
        // kind maps to (3DObject / Building / PointCloud) instead of a hardcoded
        // 3DObject.
        var layerType = I3sLayerTypeMap.ToLayerType(datasetType);
        var storeProfile = I3sLayerTypeMap.ToStoreProfile(datasetType);

        var store = new I3sStore
        {
            Id = scene.Id,
            Profile = storeProfile,
            Version = I3sVersion,
            NormalReferenceFrame = "east-north-up",
        };

        // #1809: once the layer's tileset projects to fetchable node pages, the
        // store advertises store.nodePages so a conformant client traverses the
        // layer through nodepages/{n}. When no node pages are available (e.g. a
        // config-registry scene with no loadable tileset) the store stays a
        // descriptor-only block rather than advertising a node URL that 404s.
        if (advertiseNodePages)
        {
            store.NodePages = new I3sNodePageDefinition
            {
                NodesPerPage = I3sNodePageProjector.NodesPerPage,
                LodSelectionMetricType = I3sNodePageProjector.LodSelectionMetricType,
            };
            store.DefaultGeometrySchema = DefaultGeometryDefinitions[0];
        }

        return new I3sSceneLayerDocument
        {
            Id = LayerId,
            LayerType = layerType,
            Name = scene.Name,
            Description = scene.Description,
            Version = I3sVersion,
            SpatialReference = spatialReference,
            HeightModelInfo = new I3sHeightModelInfo
            {
                HeightModel = "ellipsoidal",
                VertCrs = "meter",
                HeightUnit = "meter",
            },
            FullExtent = fullExtent,
            Store = store,
            // Format definitions/schema that DESCRIBE the served format
            // (vertex/material/texture/attribute layout) and bind the node-page
            // resource references emitted by I3sNodePageProjector.
            AttributeStorageInfo = DefaultAttributeStorageInfo,
            GeometryDefinitions = DefaultGeometryDefinitions,
            MaterialDefinitions = DefaultMaterialDefinitions,
            TextureSetDefinitions = DefaultTextureSetDefinitions,
        };
    }

    /// <summary>
    /// Per-attribute storage layout describing the default <c>OBJECTID</c> key
    /// (<c>f_0</c>, <c>Oid32</c>) every served 3D Object node carries. The
    /// <c>f_0</c> attribute file is fetchable at
    /// <c>nodes/{id}/attributes/f_0/0</c> (#1811, identify parity); user-attribute
    /// fields are descriptive only until the deferred property-table decode lands.
    /// </summary>
    private static readonly IReadOnlyList<I3sAttributeStorageInfo> DefaultAttributeStorageInfo =
    [
        new I3sAttributeStorageInfo
        {
            Key = "f_0",
            Name = "OBJECTID",
            Ordering = ["attributeValues"],
            AttributeValues = new I3sAttributeValues
            {
                ValueType = "Oid32",
                ValuesPerElement = 1,
            },
        },
    ];

    /// <summary>
    /// Default node geometry schema: a single uncompressed buffer carrying
    /// interleaved position/normal/uv0 vertex streams. Describes the format the
    /// hosted 3D Tiles content maps to; no geometry buffer is fetchable here.
    /// </summary>
    private static readonly IReadOnlyList<I3sGeometryDefinition> DefaultGeometryDefinitions =
    [
        new I3sGeometryDefinition
        {
            GeometryBuffers =
            [
                new I3sGeometryBuffer
                {
                    Position = new I3sVertexLayout { Type = "Float32", Component = 3 },
                    Normal = new I3sVertexLayout { Type = "Float32", Component = 3 },
                    Uv0 = new I3sVertexLayout { Type = "Float32", Component = 2 },
                },
            ],
        },
    ];

    /// <summary>
    /// Default PBR material the served geometry references. Descriptive only.
    /// </summary>
    private static readonly IReadOnlyList<I3sMaterialDefinition> DefaultMaterialDefinitions =
    [
        new I3sMaterialDefinition
        {
            AlphaMode = "OPAQUE",
            DoubleSided = false,
            PbrMetallicRoughness = new I3sPbrMetallicRoughness
            {
                BaseColorFactor = [1.0, 1.0, 1.0, 1.0],
                MetallicFactor = 0.0,
                RoughnessFactor = 1.0,
            },
        },
    ];

    /// <summary>
    /// Default texture-set the served materials reference (JPEG). Descriptive
    /// only.
    /// </summary>
    private static readonly IReadOnlyList<I3sTextureSetDefinition> DefaultTextureSetDefinitions =
    [
        new I3sTextureSetDefinition
        {
            Formats =
            [
                new I3sTextureFormat { Name = "0", Format = "jpg" },
            ],
        },
    ];

    /// <summary>
    /// Builds the I3S SceneServer service descriptor wrapping a single scene
    /// layer.
    /// </summary>
    /// <param name="scene">The hosted scene dataset.</param>
    /// <param name="extent">
    /// The scene's WGS-84 extent (optionally z-bearing), when known.
    /// </param>
    /// <param name="datasetType">
    /// The dataset's I3S source kind, mapped to the advertised <c>layerType</c>
    /// and store <c>profile</c> (#1812).
    /// </param>
    /// <param name="advertiseNodePages">
    /// When <see langword="true"/>, the layer store advertises <c>nodePages</c>
    /// so a client traverses the layer through <c>nodepages/{n}</c> (#1809).
    /// </param>
    public static I3sSceneServiceDocument BuildService(
        SceneDataset scene,
        SceneExtent? extent,
        SceneDatasetType datasetType = SceneDatasetType.HostedTiles,
        bool advertiseNodePages = false)
    {
        ArgumentNullException.ThrowIfNull(scene);

        return new I3sSceneServiceDocument
        {
            ServiceName = scene.Name,
            ServiceVersion = I3sVersion,
            SupportedBindings = ["REST"],
            Layers = [BuildLayer(scene, extent, datasetType, advertiseNodePages)],
        };
    }
}
