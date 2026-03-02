import { describe, it, expect } from "vitest";
import { execute, schema } from "../../src/tools/describe-layer.js";
import { createMockClient, asClient } from "../test-helpers.js";

describe("honua_describe_layer", () => {
  it("returns formatted layer metadata", async () => {
    const mock = createMockClient();
    const result = await execute(asClient(mock), schema.parse({ serviceId: "Parks", layerId: 0 }));
    const parsed = JSON.parse(result.content[0].text);

    expect(parsed.id).toBe(0);
    expect(parsed.name).toBe("Test Layer");
    expect(parsed.description).toBe("A test layer");
    expect(parsed.geometryType).toBe("esriGeometryPoint");
    expect(parsed.fields).toHaveLength(3);
    expect(parsed.fields[0]).toEqual({ name: "OBJECTID", type: "esriFieldTypeOID", alias: "OBJECTID" });
    expect(parsed.fields[1]).toEqual({ name: "NAME", type: "esriFieldTypeString", alias: "Feature Name" });
    expect(parsed.extent).toEqual({ xmin: -180, ymin: -90, xmax: 180, ymax: 90 });
    expect(parsed.spatialReference).toEqual({ wkid: 4326 });
    expect(parsed.relationships).toHaveLength(1);
  });

  it("passes correct serviceId and layerId to client", async () => {
    const mock = createMockClient();
    await execute(asClient(mock), schema.parse({ serviceId: "Census", layerId: 3 }));

    expect(mock.getLayerMetadata).toHaveBeenCalledWith("Census", 3);
  });
});
