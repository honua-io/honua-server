import { describe, expect, it } from "vitest";

import { GraphicCompat } from "../src/index.js";

describe("GraphicCompat", () => {
  it("stores construction options and supports updates", () => {
    const graphic = new GraphicCompat({
      geometry: { x: -157.8, y: 21.3 },
      symbol: { type: "simple-marker" },
      attributes: { OBJECTID: 7, status: "active" },
      popupTemplate: { title: "{status}" },
    });

    graphic.setGeometry({ x: -157.7, y: 21.4 });
    graphic.setSymbol({ type: "picture-marker" });
    graphic.setAttributes({ OBJECTID: 8, status: "inactive" });

    expect(graphic.geometry).toEqual({ x: -157.7, y: 21.4 });
    expect(graphic.symbol).toEqual({ type: "picture-marker" });
    expect(graphic.attributes).toEqual({ OBJECTID: 8, status: "inactive" });
  });

  it("clones state and serializes to JSON", () => {
    const graphic = new GraphicCompat({
      geometry: { type: "point", x: 10, y: 5 },
      symbol: { type: "simple-marker", color: "blue" },
      attributes: { OBJECTID: 99 },
      popupTemplate: { title: "Parcel 99" },
      layer: { id: "parcels" },
    });

    const clone = graphic.clone();
    expect(clone).not.toBe(graphic);
    expect(clone.toJSON()).toEqual(graphic.toJSON());

    clone.setAttributes({ OBJECTID: 100 });
    expect(graphic.attributes).toEqual({ OBJECTID: 99 });
  });
});
