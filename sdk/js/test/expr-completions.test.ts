import { describe, expect, it } from "vitest";

import { collator, expr, format, get, hsl, hsla, numberFormat, resolvedLocale, toRgba } from "../src/index.js";

describe("Expression engine completions (Direction 13)", () => {
  describe("format()", () => {
    it("serializes a single text segment", () => {
      const e = format(["hello"]);
      expect(e.toJSON()).toEqual(["format", "hello", {}]);
    });

    it("serializes multi-segment with options", () => {
      const e = format([get("name"), { "font-scale": 1.2, "text-color": "#000" }], ["\n"], [get("description")]);
      expect(e.toJSON()).toEqual([
        "format",
        ["get", "name"],
        { "font-scale": 1.2, "text-color": "#000" },
        "\n",
        {},
        ["get", "description"],
        {},
      ]);
    });

    it("is accessible from expr namespace", () => {
      const e = expr.format(["test"]);
      expect(e.toJSON()).toEqual(["format", "test", {}]);
    });
  });

  describe("numberFormat()", () => {
    it("serializes with no options", () => {
      const e = numberFormat(42);
      expect(e.toJSON()).toEqual(["number-format", 42, {}]);
    });

    it("serializes with locale and fraction digits", () => {
      const e = numberFormat(get("price"), {
        locale: "en-US",
        currency: "USD",
        "min-fraction-digits": 2,
        "max-fraction-digits": 2,
      });
      expect(e.toJSON()).toEqual([
        "number-format",
        ["get", "price"],
        {
          locale: "en-US",
          currency: "USD",
          "min-fraction-digits": 2,
          "max-fraction-digits": 2,
        },
      ]);
    });

    it("is accessible from expr namespace", () => {
      const e = expr.numberFormat(100);
      expect(e.toJSON()).toEqual(["number-format", 100, {}]);
    });
  });

  describe("hsl() / hsla()", () => {
    it("serializes hsl", () => {
      const e = hsl(120, 50, 50);
      expect(e.toJSON()).toEqual(["hsl", 120, 50, 50]);
    });

    it("serializes hsla", () => {
      const e = hsla(240, 100, 50, 0.5);
      expect(e.toJSON()).toEqual(["hsla", 240, 100, 50, 0.5]);
    });

    it("accepts expression inputs", () => {
      const e = hsl(get("hue"), 50, 50);
      expect(e.toJSON()).toEqual(["hsl", ["get", "hue"], 50, 50]);
    });

    it("is accessible from expr namespace", () => {
      expect(expr.hsl(0, 0, 0).toJSON()).toEqual(["hsl", 0, 0, 0]);
      expect(expr.hsla(0, 0, 0, 1).toJSON()).toEqual(["hsla", 0, 0, 0, 1]);
    });
  });

  describe("toRgba()", () => {
    it("serializes to-rgba", () => {
      const e = toRgba("red");
      expect(e.toJSON()).toEqual(["to-rgba", "red"]);
    });

    it("accepts expression inputs", () => {
      const e = toRgba(hsl(120, 50, 50));
      expect(e.toJSON()).toEqual(["to-rgba", ["hsl", 120, 50, 50]]);
    });

    it("is accessible from expr namespace", () => {
      expect(expr.toRgba("#fff").toJSON()).toEqual(["to-rgba", "#fff"]);
    });
  });

  describe("collator()", () => {
    it("serializes with no options", () => {
      const e = collator();
      expect(e.toJSON()).toEqual(["collator", {}]);
    });

    it("serializes with all options", () => {
      const e = collator({
        "case-sensitive": true,
        "diacritic-sensitive": false,
        locale: "de",
      });
      expect(e.toJSON()).toEqual([
        "collator",
        {
          "case-sensitive": true,
          "diacritic-sensitive": false,
          locale: "de",
        },
      ]);
    });

    it("is accessible from expr namespace", () => {
      expect(expr.collator().toJSON()).toEqual(["collator", {}]);
    });
  });

  describe("resolvedLocale()", () => {
    it("composes with collator", () => {
      const c = collator({ locale: "en" });
      const e = resolvedLocale(c);
      expect(e.toJSON()).toEqual(["resolved-locale", ["collator", { locale: "en" }]]);
    });

    it("is accessible from expr namespace", () => {
      const c = expr.collator();
      const e = expr.resolvedLocale(c);
      expect(e.toJSON()).toEqual(["resolved-locale", ["collator", {}]]);
    });
  });
});
