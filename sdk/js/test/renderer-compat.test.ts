import { describe, expect, it } from "vitest";

import {
  SimpleRendererCompat,
  UniqueValueRendererCompat,
} from "../src/index.js";

describe("renderer compat", () => {
  it("supports simple renderer payloads", () => {
    const renderer = new SimpleRendererCompat({
      symbol: { type: "simple-marker", color: "orange" },
      label: "All features",
      description: "Default symbology",
      visualVariables: [{ type: "size", field: "count" }],
    });

    expect(renderer.toJSON()).toEqual({
      symbol: { type: "simple-marker", color: "orange" },
      label: "All features",
      description: "Default symbology",
      visualVariables: [{ type: "size", field: "count" }],
    });
    expect(renderer.clone().toJSON()).toEqual(renderer.toJSON());
  });

  it("supports unique value renderer add/remove semantics", () => {
    const renderer = new UniqueValueRendererCompat({
      field: "status",
      uniqueValueInfos: [{ value: "open", label: "Open" }],
    });

    renderer.addUniqueValueInfo({ value: "closed", label: "Closed" });
    expect(renderer.uniqueValueInfos).toHaveLength(2);
    expect(renderer.removeUniqueValueInfo("open")).toBe(true);
    expect(renderer.removeUniqueValueInfo("missing")).toBe(false);
    expect(renderer.clone().toJSON()).toEqual(renderer.toJSON());
  });
});
