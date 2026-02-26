import { describe, expect, it } from "vitest";

import {
  LabelClassCompat,
  PictureMarkerSymbolCompat,
  TextSymbolCompat,
} from "../src/index.js";

describe("symbol/label compat", () => {
  it("supports picture marker and text symbols", () => {
    const picture = new PictureMarkerSymbolCompat({
      url: "https://example.test/marker.png",
      width: 24,
      height: 24,
      opacity: 0.8,
    });

    const text = new TextSymbolCompat({
      text: "Parcels",
      color: "#222",
      haloColor: "#fff",
      haloSize: 1,
      xoffset: 2,
      yoffset: -4,
    });

    expect(picture.clone().toJSON()).toEqual(picture.toJSON());
    expect(text.clone().toJSON()).toEqual(text.toJSON());
  });

  it("supports label class payloads", () => {
    const labelClass = new LabelClassCompat({
      labelExpressionInfo: { expression: "$feature.NAME" },
      symbol: { type: "text", color: "#222" },
      where: "status = 'active'",
      minScale: 0,
      maxScale: 0,
    });

    expect(labelClass.clone().toJSON()).toEqual(labelClass.toJSON());
  });
});
