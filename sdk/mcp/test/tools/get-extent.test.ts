import { describe, it, expect, vi } from "vitest";
import { execute, schema } from "../../src/tools/get-extent.js";
import { createMockClient, asClient } from "../test-helpers.js";

describe("honua_get_extent", () => {
  it("returns extent and count from response", async () => {
    const mock = createMockClient({
      queryFeatures: vi.fn().mockResolvedValue({
        extent: { xmin: -120, ymin: 30, xmax: -110, ymax: 40 },
        count: 15,
      }),
    });
    const result = await execute(asClient(mock), schema.parse({ serviceId: "Parks", layerId: 0 }));
    const parsed = JSON.parse(result.content[0].text);

    expect(parsed.extent).toEqual({ xmin: -120, ymin: 30, xmax: -110, ymax: 40 });
    expect(parsed.count).toBe(15);
  });

  it("passes returnExtentOnly in extraParams", async () => {
    const mock = createMockClient({
      queryFeatures: vi.fn().mockResolvedValue({ extent: {}, count: 0 }),
    });
    await execute(asClient(mock), schema.parse({ serviceId: "Parks", layerId: 0 }));

    expect(mock.queryFeatures).toHaveBeenCalledWith(
      expect.objectContaining({
        extraParams: { returnExtentOnly: true },
        returnGeometry: false,
      }),
    );
  });

  it("returns null extent when backend returns count-only payload", async () => {
    const mock = createMockClient({
      queryFeatures: vi.fn().mockResolvedValue({ count: 0 }),
    });
    const result = await execute(asClient(mock), schema.parse({ serviceId: "Parks", layerId: 0 }));
    const parsed = JSON.parse(result.content[0].text);

    expect(parsed).toEqual({ extent: null, count: 0 });
  });

  it("throws if extent payload is missing", async () => {
    const mock = createMockClient({
      queryFeatures: vi.fn().mockResolvedValue({}),
    });
    await expect(
      execute(asClient(mock), schema.parse({ serviceId: "Parks", layerId: 0 })),
    ).rejects.toThrow("Extent query did not return an extent payload");
  });
});
