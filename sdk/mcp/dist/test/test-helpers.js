import { vi } from "vitest";
export function createMockClient(overrides = {}) {
    return {
        listServices: overrides.listServices ?? vi.fn().mockResolvedValue({
            services: [
                { name: "Parks", type: "FeatureServer" },
                { name: "Basemap", type: "MapServer" },
                { name: "Census", type: "FeatureServer" },
            ],
        }),
        getFeatureServiceMetadata: overrides.getFeatureServiceMetadata ?? vi.fn().mockResolvedValue({
            serviceDescription: "Test service",
            layers: [
                { id: 0, name: "Layer 0" },
                { id: 1, name: "Layer 1" },
            ],
            spatialReference: { wkid: 4326 },
        }),
        getLayerMetadata: overrides.getLayerMetadata ?? vi.fn().mockResolvedValue({
            id: 0,
            name: "Test Layer",
            description: "A test layer",
            geometryType: "esriGeometryPoint",
            fields: [
                { name: "OBJECTID", type: "esriFieldTypeOID" },
                { name: "NAME", type: "esriFieldTypeString", alias: "Feature Name" },
                { name: "VALUE", type: "esriFieldTypeDouble" },
            ],
            extent: { xmin: -180, ymin: -90, xmax: 180, ymax: 90 },
            spatialReference: { wkid: 4326 },
            relationships: [{ id: 0, name: "rel_0", relatedTableId: 1 }],
        }),
        queryFeatures: overrides.queryFeatures ?? vi.fn().mockResolvedValue({
            objectIdFieldName: "OBJECTID",
            geometryType: "esriGeometryPoint",
            spatialReference: { wkid: 4326 },
            features: [
                { attributes: { OBJECTID: 1, NAME: "Park A", VALUE: 100 } },
                { attributes: { OBJECTID: 2, NAME: "Park B", VALUE: 200 } },
            ],
            exceededTransferLimit: false,
        }),
    };
}
export function asClient(mock) {
    return mock;
}
