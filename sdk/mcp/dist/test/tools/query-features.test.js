import { describe, it, expect, vi } from "vitest";
import { execute, schema } from "../../src/tools/query-features.js";
import { createMockClient, asClient } from "../test-helpers.js";
describe("honua_query_features", () => {
    it("returns features with returnedCount", async () => {
        const mock = createMockClient();
        const result = await execute(asClient(mock), schema.parse({ serviceId: "Parks", layerId: 0 }));
        const parsed = JSON.parse(result.content[0].text);
        expect(parsed.returnedCount).toBe(2);
        expect(parsed.features).toHaveLength(2);
        expect(parsed.features[0].attributes.NAME).toBe("Park A");
        expect(parsed.exceededTransferLimit).toBe(false);
    });
    it("omits geometry when returnGeometry is false", async () => {
        const mock = createMockClient();
        const result = await execute(asClient(mock), schema.parse({ serviceId: "Parks", layerId: 0, returnGeometry: false }));
        const parsed = JSON.parse(result.content[0].text);
        expect(parsed.features[0].geometry).toBeUndefined();
    });
    it("includes geometry when returnGeometry is true", async () => {
        const mock = createMockClient({
            queryFeatures: vi.fn().mockResolvedValue({
                features: [{ attributes: { OBJECTID: 1 }, geometry: { x: 10, y: 20 } }],
            }),
        });
        const result = await execute(asClient(mock), schema.parse({ serviceId: "Parks", layerId: 0, returnGeometry: true }));
        const parsed = JSON.parse(result.content[0].text);
        expect(parsed.features[0].geometry).toEqual({ x: 10, y: 20 });
    });
    it("maps spatialRel to Esri enum", async () => {
        const mock = createMockClient();
        await execute(asClient(mock), schema.parse({ serviceId: "Parks", layerId: 0, spatialRel: "contains" }));
        expect(mock.queryFeatures).toHaveBeenCalledWith(expect.objectContaining({ spatialRel: "esriSpatialRelContains" }));
    });
    it("clamps limit to 2000", async () => {
        const mock = createMockClient();
        await execute(asClient(mock), schema.parse({ serviceId: "Parks", layerId: 0, limit: 5000 }));
        expect(mock.queryFeatures).toHaveBeenCalledWith(expect.objectContaining({ resultRecordCount: 2000 }));
    });
    it("defaults limit to 100", async () => {
        const mock = createMockClient();
        await execute(asClient(mock), schema.parse({ serviceId: "Parks", layerId: 0 }));
        expect(mock.queryFeatures).toHaveBeenCalledWith(expect.objectContaining({ resultRecordCount: 100 }));
    });
    it("maps orderBy to orderByFields", async () => {
        const mock = createMockClient();
        await execute(asClient(mock), schema.parse({ serviceId: "Parks", layerId: 0, orderBy: "NAME ASC" }));
        expect(mock.queryFeatures).toHaveBeenCalledWith(expect.objectContaining({ orderByFields: "NAME ASC" }));
    });
});
