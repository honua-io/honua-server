import { describe, expect, it } from "vitest";

import {
  LabelClassCompat,
  PictureMarkerSymbolCompat,
  TextSymbolCompat,
} from "../src/index.js";

describe("symbol/label compat", () => {
  it("supports picture marker and text symbols", async () => {
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
    const opacityValues: unknown[] = [];
    const textValues: unknown[] = [];
    const opacityHandle = picture.watch("opacity", (value) => {
      opacityValues.push(value);
    });
    const textHandle = text.watch("text", (value) => {
      textValues.push(value);
    });

    await picture.when();
    await text.when();
    picture.update({ opacity: 0.6 });
    text.update({ text: "Parcels Updated" });
    opacityHandle.remove();
    textHandle.remove();
    const watchSnapshot = {
      opacity: opacityValues.length,
      text: textValues.length,
    };

    picture.update({ opacity: 0.5 });
    text.update({ text: "Parcels Final" });

    expect(picture.clone().toJSON()).toEqual(picture.toJSON());
    expect(text.clone().toJSON()).toEqual(text.toJSON());
    expect(opacityValues).toEqual([0.6]);
    expect(textValues).toEqual(["Parcels Updated"]);
    expect(opacityValues).toHaveLength(watchSnapshot.opacity);
    expect(textValues).toHaveLength(watchSnapshot.text);
  });

  it("supports label class payloads", async () => {
    const labelClass = new LabelClassCompat({
      labelExpressionInfo: { expression: "$feature.NAME" },
      symbol: { type: "text", color: "#222" },
      where: "status = 'active'",
      minScale: 0,
      maxScale: 0,
    });
    const whereValues: unknown[] = [];
    const whereHandle = labelClass.watch("where", (value) => {
      whereValues.push(value);
    });

    await labelClass.when();
    labelClass.update({ where: "status = 'inactive'" });
    whereHandle.remove();
    const watchSnapshot = whereValues.length;
    labelClass.update({ where: "status = 'archived'" });

    expect(labelClass.clone().toJSON()).toEqual(labelClass.toJSON());
    expect(labelClass.loaded).toBe(true);
    expect(labelClass.loadStatus).toBe("loaded");
    expect(whereValues).toEqual(["status = 'inactive'"]);
    expect(whereValues).toHaveLength(watchSnapshot);
  });
});
