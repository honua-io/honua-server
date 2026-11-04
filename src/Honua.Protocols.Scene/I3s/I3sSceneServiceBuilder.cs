// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Scene.Conversion;
using Honua.Core.Features.Scene.Domain;
using Honua.Scene;
using Honua.Scene.Assets;

namespace Honua.Protocols.Scene.I3s;

internal static class I3sSceneServiceBuilder
{
    public static I3sSceneLayerDocument BuildPersistedLayer(SceneDataset scene, I3sSceneResources? resources,
        SceneDatasetType datasetType = SceneDatasetType.HostedTiles)
    {
        var layer = BuildLayer(scene, resources?.Extent, datasetType,
            advertiseNodePages: resources is not null);
        layer.Fields = resources?.Fields ?? [];
        layer.ObjectIdField = resources is not null ? "OBJECTID" : null;
        layer.AttributeStorageInfo = resources?.Attributes ?? [];
        layer.GeometryDefinitions = resources is not null ? [I3sPersistedResourceEncoder.GeometryDefinition] : [];
        layer.MaterialDefinitions = resources?.Materials ?? [];
        layer.TextureSetDefinitions = resources?.TextureSets ?? [];
        layer.Store!.NormalReferenceFrame = "earth-centered";
        layer.Store.IndexCrs = "http://www.opengis.net/def/crs/EPSG/0/4326";
        layer.Store.VertexCrs = layer.Store.IndexCrs;
        layer.Store.DefaultGeometrySchema = resources is not null ? I3sPersistedResourceEncoder.GeometrySchema : null;
        return layer;
    }

    public const string DefaultLayerType = "3DObject";

    public const int Wgs84Wkid = 4326;

    public const int Wgs84EllipsoidVcsWkid = 115700;

    public const int LayerId = 0;

    public const string I3sVersion = "1.7";

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
        // layer advertises nodePages so a conformant client traverses the
        // layer through nodepages/{n}. When no node pages are available (e.g. a
        // config-registry scene with no loadable tileset) the store stays a
        // descriptor-only block rather than advertising a node URL that 404s.
        if (advertiseNodePages)
        {
            store.DefaultGeometrySchema = DefaultGeometrySchema;
        }

        return new I3sSceneLayerDocument
        {
            Id = LayerId,
            LayerType = layerType,
            Name = scene.Name,
            Description = scene.Description,
            Version = I3sVersion,
            Capabilities = ["View"],
            SpatialReference = spatialReference,
            HeightModelInfo = new I3sHeightModelInfo
            {
                HeightModel = "ellipsoidal",
                VertCrs = "meter",
                HeightUnit = "meter",
            },
            FullExtent = fullExtent,
            Store = store,
            NodePages = advertiseNodePages
                ? new I3sNodePageDefinition
                {
                    NodesPerPage = I3sNodePageProjector.NodesPerPage,
                    LodSelectionMetricType = I3sNodePageProjector.LodSelectionMetricType,
                }
                : null,
            // Format definitions/schema that DESCRIBE the served format
            // (vertex/material/texture/attribute layout) and bind the node-page
            // resource references emitted by I3sNodePageProjector.
            AttributeStorageInfo = DefaultAttributeStorageInfo,
            GeometryDefinitions = DefaultGeometryDefinitions,
            MaterialDefinitions = DefaultMaterialDefinitions,
            TextureSetDefinitions = DefaultTextureSetDefinitions,
        };
    }

    private static readonly IReadOnlyList<I3sAttributeStorageInfo> DefaultAttributeStorageInfo =
    [
        new I3sAttributeStorageInfo
        {
            Key = "f_0",
            Name = "OBJECTID",
            Header =
            [
                new I3sAttributeHeader { Property = "count", ValueType = "UInt32" },
            ],
            Ordering = ["attributeValues"],
            AttributeValues = new I3sAttributeValues
            {
                ValueType = "Oid32",
                ValuesPerElement = 1,
            },
        },
    ];

    private static readonly IReadOnlyList<I3sGeometryDefinition> DefaultGeometryDefinitions =
    [
        new I3sGeometryDefinition
        {
            Topology = "triangle",
            GeometryBuffers =
            [
                new I3sGeometryBuffer
                {
                    Offset = 8,
                    Position = new I3sVertexLayout { Type = "Float32", Component = 3 },
                    Normal = new I3sVertexLayout { Type = "Float32", Component = 3 },
                    Uv0 = new I3sVertexLayout { Type = "Float32", Component = 2 },
                    Color = new I3sVertexLayout { Type = "UInt8", Component = 4 },
                    FeatureId = new I3sVertexLayout { Type = "UInt64", Component = 1, Binding = "per-feature" },
                    FaceRange = new I3sVertexLayout { Type = "UInt32", Component = 2, Binding = "per-feature" },
                },
            ],
        },
    ];

    private static readonly I3sDefaultGeometrySchema DefaultGeometrySchema = new()
    {
        GeometryType = "triangles",
        Header =
        [
            new I3sGeometryHeader { Property = "vertexCount", Type = "UInt32" },
            new I3sGeometryHeader { Property = "featureCount", Type = "UInt32" },
        ],
        Topology = "PerAttributeArray",
        Ordering = ["position", "normal", "uv0", "color"],
        VertexAttributes = new Dictionary<string, I3sAttributeValues>
        {
            ["position"] = new() { ValueType = "Float32", ValuesPerElement = 3 },
            ["normal"] = new() { ValueType = "Float32", ValuesPerElement = 3 },
            ["uv0"] = new() { ValueType = "Float32", ValuesPerElement = 2 },
            ["color"] = new() { ValueType = "UInt8", ValuesPerElement = 4 },
        },
        FeatureAttributeOrder = ["id", "faceRange"],
        FeatureAttributes = new Dictionary<string, I3sAttributeValues>
        {
            ["id"] = new() { ValueType = "UInt64", ValuesPerElement = 1 },
            ["faceRange"] = new() { ValueType = "UInt32", ValuesPerElement = 2 },
        },
    };

    private static readonly IReadOnlyList<I3sMaterialDefinition> DefaultMaterialDefinitions =
    [
        new I3sMaterialDefinition
        {
            AlphaMode = "opaque",
            DoubleSided = false,
            PbrMetallicRoughness = new I3sPbrMetallicRoughness
            {
                BaseColorFactor = [1.0, 1.0, 1.0, 1.0],
                MetallicFactor = 0.0,
                RoughnessFactor = 1.0,
            },
        },
    ];

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
