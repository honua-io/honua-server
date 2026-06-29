// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server;

public static partial class EndpointRegistry
{
    // Expression-bodied (computed) so it is a method, not a static field
    // initializer; this keeps `All` independent of cross-file static-init order.
    private static IReadOnlyList<EndpointDefinition> SceneTileEndpoints =>
    [
        new("POST", "/api/v1/cloud-demo/reset"),
        new("GET", "/api/v1/realtime/incidents/sse"),
        new("GET", "/tiles/{layerId}/{z}/{x}/{y}.mvt"),
        new("GET", "/tiles/{layerId}/h3/{z}/{x}/{y}.mvt"),
        new("GET", "/tiles/{layerId}/tile.json"),
        new("GET", "/terrain/{datasetId}/tile.json"),
        new("GET", "/terrain/{datasetId}/{z}/{x}/{y}.png"),

        // Saved-map collaboration session seam.
        new("POST", "/api/v1/saved-maps/{mapId}/collaboration/sessions/join"),
        // Realtime presence/cursor/follow WebSocket transport (#971/#1290).
        new("GET", "/api/v1/saved-maps/{mapId}/collaboration/sessions/stream"),
        new("POST", "/api/v1/saved-maps/{mapId}/collaboration/feature-locks/claim"),
        new("POST", "/api/v1/saved-maps/{mapId}/collaboration/feature-locks/renew"),
        new("POST", "/api/v1/saved-maps/{mapId}/collaboration/feature-locks/release"),
        // Durable collaborative edit op-log: append (cursor + idempotency) and replay (#972).
        new("POST", "/api/v1/saved-maps/{mapId}/collaboration/operations"),
        new("GET", "/api/v1/saved-maps/{mapId}/collaboration/operations"),

        // Public SDK-compatible scene discovery (#923).
        new("GET", "/api/scenes"),
        new("GET", "/api/scenes/{sceneId}"),
        new("GET", "/api/scenes/{sceneId}/resolve"),

        // Hosted 3D Tiles scene serving (#837).
        new("POST", "/scenes/{sceneId}/access-envelope"),
        new("GET", "/scenes/{sceneId}/exports/openusd/stage.usda"),
        new("GET", "/scenes/{sceneId}/tileset.json"),
        new("HEAD", "/scenes/{sceneId}/tileset.json"),

        // Esri I3S SceneServer serving at the canonical GeoServices path
        // (#1806; Enterprise-gated). The /scenes/{sceneId}/SceneServer routes
        // below are retained as documented aliases (#1202).
        new("GET", "/rest/services/{sceneId}/SceneServer"),
        new("GET", "/rest/services/{sceneId}/SceneServer/layers/{layerId:int}"),
        new("GET", "/scenes/{sceneId}/SceneServer"),
        new("GET", "/scenes/{sceneId}/SceneServer/layers/{layerId:int}"),

        // I3S node-page traversal (#1809) and per-field statistics (#1811),
        // served at the canonical GeoServices path plus the /scenes alias.
        new("GET", "/rest/services/{sceneId}/SceneServer/layers/{layerId:int}/nodepages/{pageId:int}"),
        new("GET", "/rest/services/{sceneId}/SceneServer/layers/{layerId:int}/statistics/{fieldKey}/0"),
        new("GET", "/scenes/{sceneId}/SceneServer/layers/{layerId:int}/nodepages/{pageId:int}"),
        new("GET", "/scenes/{sceneId}/SceneServer/layers/{layerId:int}/statistics/{fieldKey}/0"),

        // I3S node geometry binary resource (#1810): transcoded renderable
        // geometry served at the node/geometries path (GeoServices + /scenes alias).
        new("GET", "/rest/services/{sceneId}/SceneServer/layers/{layerId:int}/nodes/{nodeId:int}/geometries/{geometryId:int}"),
        new("GET", "/scenes/{sceneId}/SceneServer/layers/{layerId:int}/nodes/{nodeId:int}/geometries/{geometryId:int}"),

        // I3S per-field attribute binary resource (#2234): per-node attribute
        // buffer served at the node/attributes path (GeoServices + /scenes alias).
        new("GET", "/rest/services/{sceneId}/SceneServer/layers/{layerId:int}/nodes/{nodeId:int}/attributes/{fieldKey}/{attributeId:int}"),
        new("GET", "/scenes/{sceneId}/SceneServer/layers/{layerId:int}/nodes/{nodeId:int}/attributes/{fieldKey}/{attributeId:int}"),

        new("GET", "/scenes/{sceneId}/{*assetPath}"),
        new("HEAD", "/scenes/{sceneId}/{*assetPath}"),

        // Scene dataset registry admin endpoints (#844).
        new("GET", "/api/v1/admin/scenes"),
        new("POST", "/api/v1/admin/scenes"),
        new("GET", "/api/v1/admin/scenes/{id}"),
        new("PUT", "/api/v1/admin/scenes/{id}"),
        new("DELETE", "/api/v1/admin/scenes/{id}"),
        new("GET", "/api/v1/admin/scenes/{id}/resolve"),

        // 3D Tiles generation admin endpoint (#842).
        new("POST", "/api/v1/admin/scenes/generate"),

        // CityGML/BIM scene ingest admin endpoint (#1207).
        new("POST", "/api/v1/admin/scenes/ingest/citygml"),

        // LAS/LAZ/COPC point-cloud scene ingest admin endpoint (#1201).
        new("POST", "/api/v1/admin/scenes/ingest/pointcloud"),

        // Network-dataset registry editing admin endpoints (#1882).
        new("GET", "/api/v1/admin/network-datasets"),
        new("POST", "/api/v1/admin/network-datasets"),
        new("GET", "/api/v1/admin/network-datasets/{id}"),
        new("PUT", "/api/v1/admin/network-datasets/{id}"),
        new("DELETE", "/api/v1/admin/network-datasets/{id}"),

        new("GET", "/elevation/{datasetId}/value"),
        new("GET", "/elevation/{datasetId}/profile"),

        // Scene analysis on the elevation surface (#1204).
        new("POST", "/elevation/{datasetId}/sun-shadow"),
        new("POST", "/elevation/{datasetId}/slice"),
        // 3D visibility analysis on the elevation surface (#1203).
        new("POST", "/elevation/{datasetId}/line-of-sight"),
        new("POST", "/elevation/{datasetId}/viewshed"),

        new("GET", "/api/styles/{layerId}.json"),

        // Durable PMTiles publish range proxy (#845).
        new("GET", "/api/v1/tiles/pmtiles/{*artifactId}"),
        new("HEAD", "/api/v1/tiles/pmtiles/{*artifactId}"),
    ];
}
