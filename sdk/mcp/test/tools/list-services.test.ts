import { describe, it, expect, vi } from "vitest";
import { execute, schema } from "../../src/tools/list-services.js";
import { createMockClient, asClient } from "../test-helpers.js";

describe("honua_list_services", () => {
  it("returns only FeatureServer services without details", async () => {
    const mock = createMockClient();
    const result = await execute(asClient(mock), schema.parse({}));
    const parsed = JSON.parse(result.content[0].text);

    expect(parsed).toHaveLength(2);
    expect(parsed[0]).toEqual({ serviceId: "Parks", type: "FeatureServer" });
    expect(parsed[1]).toEqual({ serviceId: "Census", type: "FeatureServer" });
    expect(mock.getFeatureServiceMetadata).not.toHaveBeenCalled();
  });

  it("fetches metadata when includeDetails is true", async () => {
    const mock = createMockClient();
    const result = await execute(asClient(mock), schema.parse({ includeDetails: true }));
    const parsed = JSON.parse(result.content[0].text);

    expect(parsed).toHaveLength(2);
    expect(parsed[0]).toMatchObject({
      serviceId: "Parks",
      description: "Test service",
      layerCount: 2,
      spatialReference: { wkid: 4326 },
      metadataError: null,
    });
    expect(mock.getFeatureServiceMetadata).toHaveBeenCalledTimes(2);
  });

  it("surfaces metadata fetch failure", async () => {
    const mock = createMockClient({
      getFeatureServiceMetadata: vi.fn().mockRejectedValue(new Error("Network error")),
    });
    const result = await execute(asClient(mock), schema.parse({ includeDetails: true }));
    const parsed = JSON.parse(result.content[0].text);

    expect(parsed).toHaveLength(2);
    expect(parsed[0]).toMatchObject({
      serviceId: "Parks",
      description: null,
      layerCount: null,
      metadataError: "Network error",
    });
  });
});
