import { describe, expect, it } from "vitest";

import { ColorCompat } from "../src/index.js";

describe("ColorCompat", () => {
  it("normalizes common ArcGIS color inputs", () => {
    const cssColor = new ColorCompat("#ff6600");
    const rgbaColor = new ColorCompat([255, 102, 0, 0.8]);
    const objectColor = new ColorCompat({ r: 255, g: 102, b: 0, a: 0.5 });

    expect(cssColor.toCss()).toBe("#ff6600");
    expect(rgbaColor.toCss()).toBe("rgba(255, 102, 0, 0.8)");
    expect(objectColor.toCss(false)).toBe("rgb(255, 102, 0)");
  });

  it("clones and serializes safely", () => {
    const color = new ColorCompat([12, 34, 56, 1]);
    const clone = color.clone();

    expect(clone).not.toBe(color);
    expect(clone.toJSON()).toEqual(color.toJSON());
  });
});
