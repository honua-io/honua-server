import { describe, expect, it } from "vitest";

import {
  Expr,
  expr,
  get,
  has,
  at,
  contains,
  indexOf,
  slice,
  length,
  id,
  geometryType,
  properties,
  featureState,
  literal,
  toBoolean,
  toNumber,
  exprToString,
  toColor,
  typeOf,
  eq,
  neq,
  lt,
  lte,
  gt,
  gte,
  not,
  all,
  any,
  switchCase,
  matchExpr,
  coalesce,
  add,
  subtract,
  multiply,
  divide,
  mod,
  pow,
  abs,
  ceil,
  floor,
  round,
  sqrt,
  ln,
  log2,
  log10,
  sin,
  cos,
  tan,
  asin,
  acos,
  atan,
  min,
  max,
  e,
  pi,
  ln2Const,
  concat,
  upcase,
  downcase,
  rgb,
  rgba,
  step,
  interpolate,
  interpolateHcl,
  interpolateLab,
  linear,
  exponential,
  cubicBezier,
  zoom,
  letExpr,
  varExpr,
  image,
} from "../src/index.js";

describe("Expr", () => {
  it("wraps a JSON value and serializes via toJSON()", () => {
    const e = new Expr(["get", "name"]);
    expect(e.toJSON()).toEqual(["get", "name"]);
  });

  it("serializes raw primitives", () => {
    const e = new Expr(42);
    expect(e.toJSON()).toBe(42);
  });
});

describe("expr namespace", () => {
  it("exposes all builder functions", () => {
    expect(typeof expr.get).toBe("function");
    expect(typeof expr.step).toBe("function");
    expect(typeof expr.interpolate).toBe("function");
    expect(typeof expr.case).toBe("function");
    expect(typeof expr.match).toBe("function");
    expect(typeof expr.let).toBe("function");
    expect(typeof expr.var).toBe("function");
    expect(typeof expr.toString).toBe("function");
    expect(typeof expr.ln2).toBe("function");
  });
});

describe("Lookup expressions", () => {
  it("get() serializes to property lookup", () => {
    expect(get("name").toJSON()).toEqual(["get", "name"]);
  });

  it("get() with object serializes two-arg form", () => {
    expect(get("key", properties()).toJSON()).toEqual([
      "get",
      "key",
      ["properties"],
    ]);
  });

  it("has() serializes to property existence test", () => {
    expect(has("name").toJSON()).toEqual(["has", "name"]);
  });

  it("at() serializes to array index access", () => {
    expect(at(0, literal([1, 2, 3])).toJSON()).toEqual([
      "at",
      0,
      ["literal", [1, 2, 3]],
    ]);
  });

  it("contains() serializes to 'in' expression", () => {
    expect(contains("a", get("tags")).toJSON()).toEqual([
      "in",
      "a",
      ["get", "tags"],
    ]);
  });

  it("indexOf() serializes with optional fromIndex", () => {
    expect(indexOf("x", get("name")).toJSON()).toEqual([
      "index-of",
      "x",
      ["get", "name"],
    ]);
    expect(indexOf("x", get("name"), 2).toJSON()).toEqual([
      "index-of",
      "x",
      ["get", "name"],
      2,
    ]);
  });

  it("slice() serializes with optional end", () => {
    expect(slice(get("name"), 0, 3).toJSON()).toEqual([
      "slice",
      ["get", "name"],
      0,
      3,
    ]);
    expect(slice(get("name"), 1).toJSON()).toEqual([
      "slice",
      ["get", "name"],
      1,
    ]);
  });

  it("length() serializes to length expression", () => {
    expect(length(get("name")).toJSON()).toEqual(["length", ["get", "name"]]);
  });
});

describe("Feature data expressions", () => {
  it("id() serializes to feature id", () => {
    expect(id().toJSON()).toEqual(["id"]);
  });

  it("geometryType() serializes to geometry-type", () => {
    expect(geometryType().toJSON()).toEqual(["geometry-type"]);
  });

  it("properties() serializes to properties", () => {
    expect(properties().toJSON()).toEqual(["properties"]);
  });

  it("featureState() serializes to feature-state lookup", () => {
    expect(featureState("hover").toJSON()).toEqual(["feature-state", "hover"]);
  });
});

describe("Type coercion expressions", () => {
  it("literal() wraps arrays and objects", () => {
    expect(literal([1, 2]).toJSON()).toEqual(["literal", [1, 2]]);
    expect(literal({ a: 1 }).toJSON()).toEqual(["literal", { a: 1 }]);
  });

  it("toBoolean() serializes to to-boolean", () => {
    expect(toBoolean(get("active")).toJSON()).toEqual([
      "to-boolean",
      ["get", "active"],
    ]);
  });

  it("toNumber() accepts variadic fallbacks", () => {
    expect(toNumber(get("val"), 0).toJSON()).toEqual([
      "to-number",
      ["get", "val"],
      0,
    ]);
  });

  it("exprToString() serializes to to-string", () => {
    expect(exprToString(get("id")).toJSON()).toEqual([
      "to-string",
      ["get", "id"],
    ]);
  });

  it("toColor() accepts variadic fallbacks", () => {
    expect(toColor(get("color"), "#000").toJSON()).toEqual([
      "to-color",
      ["get", "color"],
      "#000",
    ]);
  });

  it("typeOf() serializes to typeof", () => {
    expect(typeOf(get("val")).toJSON()).toEqual(["typeof", ["get", "val"]]);
  });
});

describe("Comparison expressions", () => {
  it("eq/neq serialize to ==/!=", () => {
    expect(eq(get("type"), "park").toJSON()).toEqual([
      "==",
      ["get", "type"],
      "park",
    ]);
    expect(neq(get("type"), "park").toJSON()).toEqual([
      "!=",
      ["get", "type"],
      "park",
    ]);
  });

  it("lt/lte/gt/gte serialize to comparison operators", () => {
    expect(lt(get("pop"), 1000).toJSON()).toEqual([
      "<",
      ["get", "pop"],
      1000,
    ]);
    expect(lte(get("pop"), 1000).toJSON()).toEqual([
      "<=",
      ["get", "pop"],
      1000,
    ]);
    expect(gt(get("pop"), 1000).toJSON()).toEqual([
      ">",
      ["get", "pop"],
      1000,
    ]);
    expect(gte(get("pop"), 1000).toJSON()).toEqual([
      ">=",
      ["get", "pop"],
      1000,
    ]);
  });
});

describe("Logic expressions", () => {
  it("not() serializes to logical negation", () => {
    expect(not(has("name")).toJSON()).toEqual(["!", ["has", "name"]]);
  });

  it("all() serializes variadic boolean inputs", () => {
    expect(
      all(has("name"), gt(get("pop"), 0)).toJSON(),
    ).toEqual(["all", ["has", "name"], [">", ["get", "pop"], 0]]);
  });

  it("any() serializes variadic boolean inputs", () => {
    expect(
      any(eq(get("type"), "park"), eq(get("type"), "garden")).toJSON(),
    ).toEqual([
      "any",
      ["==", ["get", "type"], "park"],
      ["==", ["get", "type"], "garden"],
    ]);
  });
});

describe("Decision expressions", () => {
  it("switchCase() serializes branches and fallback", () => {
    const result = switchCase(
      [gt(get("pop"), 1_000_000), "large"],
      [gt(get("pop"), 100_000), "medium"],
      "small",
    );
    expect(result.toJSON()).toEqual([
      "case",
      [">", ["get", "pop"], 1000000],
      "large",
      [">", ["get", "pop"], 100000],
      "medium",
      "small",
    ]);
  });

  it("matchExpr() serializes scalar and array labels", () => {
    const result = matchExpr(
      get("type"),
      ["residential", "#0f0"],
      [["commercial", "retail"], "#00f"],
      "#888",
    );
    expect(result.toJSON()).toEqual([
      "match",
      ["get", "type"],
      "residential",
      "#0f0",
      ["commercial", "retail"],
      "#00f",
      "#888",
    ]);
  });

  it("coalesce() serializes variadic expressions", () => {
    expect(
      coalesce(get("name_en"), get("name")).toJSON(),
    ).toEqual(["coalesce", ["get", "name_en"], ["get", "name"]]);
  });
});

describe("Math expressions", () => {
  it("add/multiply accept variadic inputs", () => {
    expect(add(1, 2, 3).toJSON()).toEqual(["+", 1, 2, 3]);
    expect(multiply(get("a"), get("b")).toJSON()).toEqual([
      "*",
      ["get", "a"],
      ["get", "b"],
    ]);
  });

  it("subtract() supports negation (1 arg) and subtraction (2 args)", () => {
    expect(subtract(5, 3).toJSON()).toEqual(["-", 5, 3]);
    expect(subtract(get("val")).toJSON()).toEqual(["-", ["get", "val"]]);
  });

  it("divide/mod/pow serialize binary operators", () => {
    expect(divide(10, 3).toJSON()).toEqual(["/", 10, 3]);
    expect(mod(10, 3).toJSON()).toEqual(["%", 10, 3]);
    expect(pow(2, 8).toJSON()).toEqual(["^", 2, 8]);
  });

  it("unary math functions serialize correctly", () => {
    expect(abs(subtract(0, 5)).toJSON()).toEqual(["abs", ["-", 0, 5]]);
    expect(ceil(1.5).toJSON()).toEqual(["ceil", 1.5]);
    expect(floor(1.5).toJSON()).toEqual(["floor", 1.5]);
    expect(round(1.5).toJSON()).toEqual(["round", 1.5]);
    expect(sqrt(16).toJSON()).toEqual(["sqrt", 16]);
    expect(ln(10).toJSON()).toEqual(["ln", 10]);
    expect(log2(8).toJSON()).toEqual(["log2", 8]);
    expect(log10(100).toJSON()).toEqual(["log10", 100]);
  });

  it("trig functions serialize correctly", () => {
    expect(sin(0).toJSON()).toEqual(["sin", 0]);
    expect(cos(0).toJSON()).toEqual(["cos", 0]);
    expect(tan(0).toJSON()).toEqual(["tan", 0]);
    expect(asin(0).toJSON()).toEqual(["asin", 0]);
    expect(acos(1).toJSON()).toEqual(["acos", 1]);
    expect(atan(1).toJSON()).toEqual(["atan", 1]);
  });

  it("min/max accept variadic inputs", () => {
    expect(min(1, 2, 3).toJSON()).toEqual(["min", 1, 2, 3]);
    expect(max(get("a"), 100).toJSON()).toEqual(["max", ["get", "a"], 100]);
  });

  it("constants serialize to zero-arg expressions", () => {
    expect(e().toJSON()).toEqual(["e"]);
    expect(pi().toJSON()).toEqual(["pi"]);
    expect(ln2Const().toJSON()).toEqual(["ln2"]);
  });
});

describe("String expressions", () => {
  it("concat() serializes variadic inputs", () => {
    expect(concat("Hello ", get("name")).toJSON()).toEqual([
      "concat",
      "Hello ",
      ["get", "name"],
    ]);
  });

  it("upcase/downcase serialize correctly", () => {
    expect(upcase(get("name")).toJSON()).toEqual(["upcase", ["get", "name"]]);
    expect(downcase("ABC").toJSON()).toEqual(["downcase", "ABC"]);
  });
});

describe("Color expressions", () => {
  it("rgb() serializes to three-component color", () => {
    expect(rgb(255, 0, 128).toJSON()).toEqual(["rgb", 255, 0, 128]);
  });

  it("rgba() serializes to four-component color", () => {
    expect(rgba(255, 0, 128, 0.5).toJSON()).toEqual([
      "rgba",
      255,
      0,
      128,
      0.5,
    ]);
  });
});

describe("Interpolation", () => {
  it("step() serializes with default and stops", () => {
    const result = step(
      get("population"),
      "#f7fbff",
      [10_000, "#6baed6"],
      [100_000, "#08306b"],
    );
    expect(result.toJSON()).toEqual([
      "step",
      ["get", "population"],
      "#f7fbff",
      10000,
      "#6baed6",
      100000,
      "#08306b",
    ]);
  });

  it("interpolate() serializes with method, input, and stops", () => {
    const result = interpolate(
      linear(),
      zoom(),
      [0, 0],
      [10, 1],
      [20, 5],
    );
    expect(result.toJSON()).toEqual([
      "interpolate",
      ["linear"],
      ["zoom"],
      0,
      0,
      10,
      1,
      20,
      5,
    ]);
  });

  it("interpolate() with exponential method", () => {
    const result = interpolate(exponential(1.5), zoom(), [5, 1], [18, 20]);
    expect(result.toJSON()).toEqual([
      "interpolate",
      ["exponential", 1.5],
      ["zoom"],
      5,
      1,
      18,
      20,
    ]);
  });

  it("interpolate() with cubic-bezier method", () => {
    const result = interpolate(
      cubicBezier(0.42, 0, 0.58, 1),
      zoom(),
      [0, 0],
      [22, 100],
    );
    expect(result.toJSON()).toEqual([
      "interpolate",
      ["cubic-bezier", 0.42, 0, 0.58, 1],
      ["zoom"],
      0,
      0,
      22,
      100,
    ]);
  });

  it("interpolateHcl() serializes with interpolate-hcl operator", () => {
    const result = interpolateHcl(
      linear(),
      get("value"),
      [0, "#ff0000"],
      [100, "#0000ff"],
    );
    expect(result.toJSON()).toEqual([
      "interpolate-hcl",
      ["linear"],
      ["get", "value"],
      0,
      "#ff0000",
      100,
      "#0000ff",
    ]);
  });

  it("interpolateLab() serializes with interpolate-lab operator", () => {
    const result = interpolateLab(
      linear(),
      get("value"),
      [0, "#ff0000"],
      [100, "#0000ff"],
    );
    expect(result.toJSON()).toEqual([
      "interpolate-lab",
      ["linear"],
      ["get", "value"],
      0,
      "#ff0000",
      100,
      "#0000ff",
    ]);
  });
});

describe("Zoom expression", () => {
  it("zoom() serializes to zero-arg expression", () => {
    expect(zoom().toJSON()).toEqual(["zoom"]);
  });
});

describe("Variable binding", () => {
  it("let/var serialize to variable binding", () => {
    const result = letExpr(
      { x: get("population"), y: 1000 },
      multiply(varExpr("x"), varExpr("y")),
    );
    const json = result.toJSON() as unknown[];
    expect(json[0]).toBe("let");
    expect(json).toContain("x");
    expect(json).toContain("y");
    expect(json[json.length - 1]).toEqual(["*", ["var", "x"], ["var", "y"]]);
  });

  it("varExpr() serializes to var reference", () => {
    expect(varExpr("count").toJSON()).toEqual(["var", "count"]);
  });
});

describe("Image expression", () => {
  it("image() serializes to image lookup", () => {
    expect(image("marker-icon").toJSON()).toEqual(["image", "marker-icon"]);
  });
});

describe("Composition (design doc examples)", () => {
  it("reproduces the step + featureState hover pattern from the design doc", () => {
    const fillColor = expr.step(
      expr.get("assessed_value"),
      "#f7fbff",
      [100_000, "#6baed6"],
      [500_000, "#08306b"],
    );

    const hoverColor = expr.case(
      [expr.featureState("hover"), "#ff0"],
      fillColor,
    );

    expect(hoverColor.toJSON()).toEqual([
      "case",
      ["feature-state", "hover"],
      "#ff0",
      [
        "step",
        ["get", "assessed_value"],
        "#f7fbff",
        100000,
        "#6baed6",
        500000,
        "#08306b",
      ],
    ]);
  });

  it("composes zoom-based interpolation with data-driven step", () => {
    const radius = expr.interpolate(
      expr.linear(),
      expr.zoom(),
      [8, expr.step(expr.get("pop"), 1, [10_000, 3], [100_000, 6])],
      [16, expr.step(expr.get("pop"), 4, [10_000, 12], [100_000, 24])],
    );

    const json = radius.toJSON() as unknown[];
    expect(json[0]).toBe("interpolate");
    expect(json[1]).toEqual(["linear"]);
    expect(json[2]).toEqual(["zoom"]);
    expect(json[3]).toBe(8);
    expect(json[5]).toBe(16);
  });

  it("builds a match expression via the expr namespace", () => {
    const color = expr.match(
      expr.get("category"),
      ["park", "#2d6a4f"],
      ["water", "#0077b6"],
      [["road", "highway"], "#adb5bd"],
      "#333",
    );

    expect(color.toJSON()).toEqual([
      "match",
      ["get", "category"],
      "park",
      "#2d6a4f",
      "water",
      "#0077b6",
      ["road", "highway"],
      "#adb5bd",
      "#333",
    ]);
  });
});
