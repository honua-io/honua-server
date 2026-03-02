import { describe, it, expect } from "vitest";
import { read, uriTemplate } from "../../src/resources/layer-schema.js";
import { createMockClient, asClient } from "../test-helpers.js";

describe("layer-schema resource", () => {
  it("has the correct URI template", () => {
    expect(uriTemplate).toBe("honua://services/{encodedServiceId}/layers/{layerId}");
  });

  it("returns formatted layer schema", async () => {
    const mock = createMockClient();
    const result = await read(asClient(mock), "Parks", "0");

    expect(result.contents).toHaveLength(1);
    expect(result.contents[0].uri).toBe("honua://services/Parks/layers/0");
    expect(result.contents[0].mimeType).toBe("application/json");

    const parsed = JSON.parse(result.contents[0].text);
    expect(parsed.name).toBe("Test Layer");
    expect(parsed.fields).toHaveLength(3);
  });

  it("decodes URL-encoded service IDs", async () => {
    const mock = createMockClient();
    await read(asClient(mock), "Folder%2FMy%20Service", "0");

    expect(mock.getLayerMetadata).toHaveBeenCalledWith("Folder/My Service", 0);
  });

  it("throws on invalid layerId", async () => {
    const mock = createMockClient();
    await expect(read(asClient(mock), "Parks", "abc")).rejects.toThrow("Invalid layerId");
  });

  it("rejects partially numeric layerId values", async () => {
    const mock = createMockClient();
    await expect(read(asClient(mock), "Parks", "12abc")).rejects.toThrow("Invalid layerId");
  });

  it("throws on invalid encoded serviceId", async () => {
    const mock = createMockClient();
    await expect(read(asClient(mock), "%E0%A4%A", "0")).rejects.toThrow("Invalid encoded serviceId");
  });
});
