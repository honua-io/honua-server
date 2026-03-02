import { describe, it, expect, vi } from "vitest";
import { read, uri } from "../../src/resources/services.js";
import { createMockClient, asClient } from "../test-helpers.js";
describe("services resource", () => {
    it("has the correct URI", () => {
        expect(uri).toBe("honua://services");
    });
    it("returns catalog with layers for each FeatureServer", async () => {
        const mock = createMockClient();
        const result = await read(asClient(mock));
        expect(result.contents).toHaveLength(1);
        expect(result.contents[0].uri).toBe("honua://services");
        expect(result.contents[0].mimeType).toBe("application/json");
        const parsed = JSON.parse(result.contents[0].text);
        expect(parsed).toHaveLength(2);
        expect(parsed[0].serviceId).toBe("Parks");
        expect(parsed[0].layers).toHaveLength(2);
        expect(parsed[1].serviceId).toBe("Census");
    });
    it("URL-encodes service IDs", async () => {
        const mock = createMockClient({
            listServices: vi.fn().mockResolvedValue({
                services: [{ name: "Folder/My Service", type: "FeatureServer" }],
            }),
        });
        const result = await read(asClient(mock));
        const parsed = JSON.parse(result.contents[0].text);
        expect(parsed[0].encodedServiceId).toBe("Folder%2FMy%20Service");
    });
    it("handles metadata errors gracefully", async () => {
        const mock = createMockClient({
            getFeatureServiceMetadata: vi.fn().mockRejectedValue(new Error("fail")),
        });
        const result = await read(asClient(mock));
        const parsed = JSON.parse(result.contents[0].text);
        expect(parsed).toHaveLength(2);
        expect(parsed[0].layers).toEqual([]);
    });
});
