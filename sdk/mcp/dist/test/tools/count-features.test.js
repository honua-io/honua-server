import { describe, it, expect, vi } from "vitest";
import { execute, schema } from "../../src/tools/count-features.js";
import { createMockClient, asClient } from "../test-helpers.js";
describe("honua_count_features", () => {
    it("returns count from response", async () => {
        const mock = createMockClient({
            queryFeatures: vi.fn().mockResolvedValue({ count: 42 }),
        });
        const result = await execute(asClient(mock), schema.parse({ serviceId: "Parks", layerId: 0 }));
        const parsed = JSON.parse(result.content[0].text);
        expect(parsed.count).toBe(42);
    });
    it("passes returnCountOnly in extraParams", async () => {
        const mock = createMockClient({
            queryFeatures: vi.fn().mockResolvedValue({ count: 10 }),
        });
        await execute(asClient(mock), schema.parse({ serviceId: "Parks", layerId: 0 }));
        expect(mock.queryFeatures).toHaveBeenCalledWith(expect.objectContaining({
            extraParams: { returnCountOnly: true },
            returnGeometry: false,
        }));
    });
    it("maps spatialRel for count queries", async () => {
        const mock = createMockClient({
            queryFeatures: vi.fn().mockResolvedValue({ count: 5 }),
        });
        await execute(asClient(mock), schema.parse({ serviceId: "Parks", layerId: 0, spatialRel: "within" }));
        expect(mock.queryFeatures).toHaveBeenCalledWith(expect.objectContaining({ spatialRel: "esriSpatialRelWithin" }));
    });
    it("defaults to 0 if count is not a number", async () => {
        const mock = createMockClient({
            queryFeatures: vi.fn().mockResolvedValue({}),
        });
        const result = await execute(asClient(mock), schema.parse({ serviceId: "Parks", layerId: 0 }));
        const parsed = JSON.parse(result.content[0].text);
        expect(parsed.count).toBe(0);
    });
});
