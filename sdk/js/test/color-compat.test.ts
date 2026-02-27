import { describe, expect, it } from "vitest";

import { ColorCompat } from "../src/index.js";

describe("ColorCompat", () => {
  it("supports when() and watch() lifecycle state", async () => {
    const color = new ColorCompat([1, 2, 3, 0.5]);
    const loadStatusValues: unknown[] = [];
    const loadedValues: unknown[] = [];
    const loadStatusHandle = color.watch("loadStatus", (value) => {
      loadStatusValues.push(value);
    });
    const loadedHandle = color.watch("loaded", (value) => {
      loadedValues.push(value);
    });

    let callbackColor: ColorCompat | undefined;
    const resolved = await color.when((readyColor) => {
      callbackColor = readyColor;
    });

    loadStatusHandle.remove();
    loadedHandle.remove();
    const watchSnapshot = {
      loadStatus: loadStatusValues.length,
      loaded: loadedValues.length,
    };

    await color.load();

    expect(resolved).toBe(color);
    expect(callbackColor).toBe(color);
    expect(color.loaded).toBe(true);
    expect(color.loadStatus).toBe("loaded");
    expect(loadStatusValues).toEqual(["loading", "loaded"]);
    expect(loadedValues).toEqual([true]);
    expect(loadStatusValues).toHaveLength(watchSnapshot.loadStatus);
    expect(loadedValues).toHaveLength(watchSnapshot.loaded);
  });

  it("normalizes common ArcGIS color inputs", () => {
    const cssColor = new ColorCompat("#ff6600");
    const rgbaColor = new ColorCompat([255, 102, 0, 0.8]);
    const objectColor = new ColorCompat({ r: 255, g: 102, b: 0, a: 0.5 });
    const values: unknown[] = [];
    const valueHandle = objectColor.watch("value", (value) => {
      values.push(value);
    });

    objectColor.set({ r: 255, g: 102, b: 0, a: 0.6 });
    valueHandle.remove();
    const watchSnapshot = values.length;
    objectColor.set({ r: 255, g: 102, b: 0, a: 0.7 });

    expect(cssColor.toCss()).toBe("#ff6600");
    expect(rgbaColor.toCss()).toBe("rgba(255, 102, 0, 0.8)");
    expect(objectColor.toCss(false)).toBe("rgb(255, 102, 0)");
    expect(values).toEqual([{ r: 255, g: 102, b: 0, a: 0.6 }]);
    expect(values).toHaveLength(watchSnapshot);
  });

  it("clones and serializes safely", () => {
    const color = new ColorCompat([12, 34, 56, 1]);
    const clone = color.clone();

    expect(clone).not.toBe(color);
    expect(clone.toJSON()).toEqual(color.toJSON());
  });
});
