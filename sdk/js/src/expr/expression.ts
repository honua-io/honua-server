/**
 * Type-safe MapLibre expression builder for the Honua SDK.
 *
 * Builds MapLibre Style Spec v8 JSON expressions with TypeScript type checking.
 * Call `.toJSON()` on any expression to get the serializable JSON array.
 *
 * @example
 * ```ts
 * import { expr } from "@honua/sdk";
 *
 * const fill = expr.step(
 *   expr.get("population"),
 *   "#f7fbff",
 *   [10_000, "#6baed6"],
 *   [100_000, "#08306b"],
 * );
 * ```
 *
 * @module
 */

// ── Branded types ────────────────────────────────────────────

/** Represents a MapLibre color value in the expression type system. */
export interface ExprColor {
  readonly __brand: "honua-expr-color";
}

/** Represents a MapLibre formatted text value in the expression type system. */
export interface ExprFormatted {
  readonly __brand: "honua-expr-formatted";
}

/** Represents a resolved image reference in the expression type system. */
export interface ExprImage {
  readonly __brand: "honua-expr-image";
}

/** Union of primitive value types in the MapLibre expression system. */
export type ExprValue = number | string | boolean | null;

// ── Input types ──────────────────────────────────────────────

/** Accepts a raw number, a number expression, or a polymorphic value expression (e.g. from `get()`). */
export type NumberInput = number | Expr<number> | Expr<ExprValue>;

/** Accepts a raw string, a string expression, or a polymorphic value expression. */
export type StringInput = string | Expr<string> | Expr<ExprValue>;

/** Accepts a raw boolean, a boolean expression, or a polymorphic value expression. */
export type BooleanInput = boolean | Expr<boolean> | Expr<ExprValue>;

/** Accepts a raw CSS color string, a color expression, or a polymorphic value expression. */
export type ColorInput = string | Expr<ExprColor> | Expr<ExprValue>;

/** Accepts any raw primitive value or any expression. Used for polymorphic positions. */
export type Resolvable = ExprValue | Expr<ExprValue> | Expr<ExprColor> | Expr<unknown>;

// ── Core Expr class ──────────────────────────────────────────

/**
 * A type-safe wrapper around a MapLibre style expression.
 *
 * The type parameter `T` tracks the expression's return type at compile time.
 * Call `.toJSON()` to serialize to the MapLibre JSON expression format.
 */
export class Expr<T = unknown> {
  /** @internal Phantom type field for compile-time safety — do not access at runtime. */
  declare readonly __type: T;

  readonly #json: unknown;

  constructor(json: unknown) {
    this.#json = json;
  }

  /** Serialize to the MapLibre JSON expression format. */
  toJSON(): unknown {
    return this.#json;
  }
}

// ── Internal helpers ─────────────────────────────────────────

function r(input: Resolvable): unknown {
  return input instanceof Expr ? input.toJSON() : input;
}

// ── Lookup ───────────────────────────────────────────────────

/** Get a feature property value, or a property from a specified object. */
export function get(property: string, object?: Expr<unknown>): Expr<ExprValue> {
  return object !== undefined
    ? new Expr<ExprValue>(["get", property, r(object)])
    : new Expr<ExprValue>(["get", property]);
}

/** Test whether a feature property exists. */
export function has(property: string, object?: Expr<unknown>): Expr<boolean> {
  return object !== undefined
    ? new Expr<boolean>(["has", property, r(object)])
    : new Expr<boolean>(["has", property]);
}

/** Get an element from an array by index. */
export function at(index: NumberInput, array: Expr<unknown>): Expr<ExprValue> {
  return new Expr<ExprValue>(["at", r(index), r(array)]);
}

/** Test whether a value is in an array or a substring is in a string. */
export function contains(needle: Resolvable, haystack: Resolvable): Expr<boolean> {
  return new Expr<boolean>(["in", r(needle), r(haystack)]);
}

/** Find the first index of a value in an array or substring in a string. Returns -1 if not found. */
export function indexOf(
  needle: Resolvable,
  haystack: Resolvable,
  fromIndex?: NumberInput,
): Expr<number> {
  return fromIndex !== undefined
    ? new Expr<number>(["index-of", r(needle), r(haystack), r(fromIndex)])
    : new Expr<number>(["index-of", r(needle), r(haystack)]);
}

/** Extract a subarray or substring. Negative indices count from the end. */
export function slice(
  input: Resolvable,
  start: NumberInput,
  end?: NumberInput,
): Expr<ExprValue> {
  return end !== undefined
    ? new Expr<ExprValue>(["slice", r(input), r(start), r(end)])
    : new Expr<ExprValue>(["slice", r(input), r(start)]);
}

/** Get the length of an array or string. */
export function length(input: Resolvable): Expr<number> {
  return new Expr<number>(["length", r(input)]);
}

// ── Feature data ─────────────────────────────────────────────

/** Get the feature's id. */
export function id(): Expr<ExprValue> {
  return new Expr<ExprValue>(["id"]);
}

/** Get the feature's geometry type ("Point", "LineString", or "Polygon"). */
export function geometryType(): Expr<string> {
  return new Expr<string>(["geometry-type"]);
}

/** Get the feature's properties object. */
export function properties(): Expr<Record<string, unknown>> {
  return new Expr<Record<string, unknown>>(["properties"]);
}

/** Read a property from the feature's programmatic state. Paint properties only. */
export function featureState(property: string): Expr<ExprValue> {
  return new Expr<ExprValue>(["feature-state", property]);
}

// ── Type coercion ────────────────────────────────────────────

/** Wrap a raw JSON array or object as a literal expression value. */
export function literal(value: unknown[] | Record<string, unknown>): Expr<ExprValue> {
  return new Expr<ExprValue>(["literal", value]);
}

/** Coerce a value to boolean. */
export function toBoolean(input: Resolvable): Expr<boolean> {
  return new Expr<boolean>(["to-boolean", r(input)]);
}

/** Coerce to number, trying each input in order until one succeeds. */
export function toNumber(...inputs: Resolvable[]): Expr<number> {
  return new Expr<number>(["to-number", ...inputs.map(r)]);
}

/** Coerce a value to string. */
export function exprToString(input: Resolvable): Expr<string> {
  return new Expr<string>(["to-string", r(input)]);
}

/** Coerce to color, trying each input in order until one succeeds. */
export function toColor(...inputs: Resolvable[]): Expr<ExprColor> {
  return new Expr<ExprColor>(["to-color", ...inputs.map(r)]);
}

/** Get the type of a value as a string ("string", "number", "boolean", "object", "array", "null"). */
export function typeOf(input: Resolvable): Expr<string> {
  return new Expr<string>(["typeof", r(input)]);
}

// ── Comparison ───────────────────────────────────────────────

/** Strict equality. */
export function eq(left: Resolvable, right: Resolvable): Expr<boolean> {
  return new Expr<boolean>(["==", r(left), r(right)]);
}

/** Strict inequality. */
export function neq(left: Resolvable, right: Resolvable): Expr<boolean> {
  return new Expr<boolean>(["!=", r(left), r(right)]);
}

/** Less than. */
export function lt(left: Resolvable, right: Resolvable): Expr<boolean> {
  return new Expr<boolean>(["<", r(left), r(right)]);
}

/** Less than or equal. */
export function lte(left: Resolvable, right: Resolvable): Expr<boolean> {
  return new Expr<boolean>(["<=", r(left), r(right)]);
}

/** Greater than. */
export function gt(left: Resolvable, right: Resolvable): Expr<boolean> {
  return new Expr<boolean>([">", r(left), r(right)]);
}

/** Greater than or equal. */
export function gte(left: Resolvable, right: Resolvable): Expr<boolean> {
  return new Expr<boolean>([">=", r(left), r(right)]);
}

// ── Logic ────────────────────────────────────────────────────

/** Logical negation. */
export function not(input: BooleanInput): Expr<boolean> {
  return new Expr<boolean>(["!", r(input)]);
}

/** True if all inputs are true. Short-circuits on first false. */
export function all(...inputs: BooleanInput[]): Expr<boolean> {
  return new Expr<boolean>(["all", ...inputs.map(r)]);
}

/** True if any input is true. Short-circuits on first true. */
export function any(...inputs: BooleanInput[]): Expr<boolean> {
  return new Expr<boolean>(["any", ...inputs.map(r)]);
}

// ── Decision ─────────────────────────────────────────────────

/**
 * First-match conditional (like if/else-if/else).
 *
 * Pass `[condition, output]` tuples followed by a fallback value:
 * ```ts
 * expr.case(
 *   [expr.gt(expr.get("pop"), 1_000_000), "large"],
 *   [expr.gt(expr.get("pop"), 100_000), "medium"],
 *   "small",
 * )
 * ```
 */
export function switchCase(
  ...args: Array<[BooleanInput, Resolvable] | Resolvable>
): Expr<ExprValue> {
  const json: unknown[] = ["case"];
  for (let i = 0; i < args.length - 1; i++) {
    const branch = args[i] as [BooleanInput, Resolvable];
    json.push(r(branch[0]), r(branch[1]));
  }
  json.push(r(args[args.length - 1] as Resolvable));
  return new Expr<ExprValue>(json);
}

/**
 * Switch-style matching on an input value.
 *
 * Pass `[label(s), output]` tuples followed by a fallback. Labels can be single
 * values or arrays for multi-match:
 * ```ts
 * expr.match(
 *   expr.get("type"),
 *   ["residential", "#0f0"],
 *   [["commercial", "retail"], "#00f"],
 *   "#888",
 * )
 * ```
 */
export function matchExpr(
  input: Resolvable,
  ...casesAndFallback: Array<[Resolvable | Resolvable[], Resolvable] | Resolvable>
): Expr<ExprValue> {
  const json: unknown[] = ["match", r(input)];
  for (let i = 0; i < casesAndFallback.length - 1; i++) {
    const branch = casesAndFallback[i] as [Resolvable | Resolvable[], Resolvable];
    const label = branch[0];
    json.push(Array.isArray(label) ? label.map(r) : r(label));
    json.push(r(branch[1]));
  }
  json.push(r(casesAndFallback[casesAndFallback.length - 1] as Resolvable));
  return new Expr<ExprValue>(json);
}

/** Return the first non-null value from the given expressions. */
export function coalesce(...inputs: Resolvable[]): Expr<ExprValue> {
  return new Expr<ExprValue>(["coalesce", ...inputs.map(r)]);
}

// ── Math ─────────────────────────────────────────────────────

/** Sum of numbers. */
export function add(...inputs: NumberInput[]): Expr<number> {
  return new Expr<number>(["+", ...inputs.map(r)]);
}

/** Subtraction (two args) or negation (one arg). */
export function subtract(a: NumberInput, b?: NumberInput): Expr<number> {
  return b !== undefined
    ? new Expr<number>(["-", r(a), r(b)])
    : new Expr<number>(["-", r(a)]);
}

/** Product of numbers. */
export function multiply(...inputs: NumberInput[]): Expr<number> {
  return new Expr<number>(["*", ...inputs.map(r)]);
}

/** Division. */
export function divide(a: NumberInput, b: NumberInput): Expr<number> {
  return new Expr<number>(["/", r(a), r(b)]);
}

/** Modulo (remainder). */
export function mod(a: NumberInput, b: NumberInput): Expr<number> {
  return new Expr<number>(["%", r(a), r(b)]);
}

/** Exponentiation: base^exponent. */
export function pow(base: NumberInput, exponent: NumberInput): Expr<number> {
  return new Expr<number>(["^", r(base), r(exponent)]);
}

/** Absolute value. */
export function abs(input: NumberInput): Expr<number> {
  return new Expr<number>(["abs", r(input)]);
}

/** Ceiling (round up). */
export function ceil(input: NumberInput): Expr<number> {
  return new Expr<number>(["ceil", r(input)]);
}

/** Floor (round down). */
export function floor(input: NumberInput): Expr<number> {
  return new Expr<number>(["floor", r(input)]);
}

/** Round to nearest integer. */
export function round(input: NumberInput): Expr<number> {
  return new Expr<number>(["round", r(input)]);
}

/** Square root. */
export function sqrt(input: NumberInput): Expr<number> {
  return new Expr<number>(["sqrt", r(input)]);
}

/** Natural logarithm. */
export function ln(input: NumberInput): Expr<number> {
  return new Expr<number>(["ln", r(input)]);
}

/** Base-2 logarithm. */
export function log2(input: NumberInput): Expr<number> {
  return new Expr<number>(["log2", r(input)]);
}

/** Base-10 logarithm. */
export function log10(input: NumberInput): Expr<number> {
  return new Expr<number>(["log10", r(input)]);
}

/** Sine (input in radians). */
export function sin(input: NumberInput): Expr<number> {
  return new Expr<number>(["sin", r(input)]);
}

/** Cosine (input in radians). */
export function cos(input: NumberInput): Expr<number> {
  return new Expr<number>(["cos", r(input)]);
}

/** Tangent (input in radians). */
export function tan(input: NumberInput): Expr<number> {
  return new Expr<number>(["tan", r(input)]);
}

/** Arcsine (result in radians). */
export function asin(input: NumberInput): Expr<number> {
  return new Expr<number>(["asin", r(input)]);
}

/** Arccosine (result in radians). */
export function acos(input: NumberInput): Expr<number> {
  return new Expr<number>(["acos", r(input)]);
}

/** Arctangent (result in radians). */
export function atan(input: NumberInput): Expr<number> {
  return new Expr<number>(["atan", r(input)]);
}

/** Minimum of the given numbers. */
export function min(...inputs: NumberInput[]): Expr<number> {
  return new Expr<number>(["min", ...inputs.map(r)]);
}

/** Maximum of the given numbers. */
export function max(...inputs: NumberInput[]): Expr<number> {
  return new Expr<number>(["max", ...inputs.map(r)]);
}

/** Euler's number (e ≈ 2.718). */
export function e(): Expr<number> {
  return new Expr<number>(["e"]);
}

/** Pi (π ≈ 3.14159). */
export function pi(): Expr<number> {
  return new Expr<number>(["pi"]);
}

/** Natural logarithm of 2 (ln(2) ≈ 0.693). */
export function ln2Const(): Expr<number> {
  return new Expr<number>(["ln2"]);
}

// ── String ───────────────────────────────────────────────────

/** Concatenate values into a string. Non-string values are coerced. */
export function concat(...inputs: Resolvable[]): Expr<string> {
  return new Expr<string>(["concat", ...inputs.map(r)]);
}

/** Convert a string to uppercase. */
export function upcase(input: StringInput): Expr<string> {
  return new Expr<string>(["upcase", r(input)]);
}

/** Convert a string to lowercase. */
export function downcase(input: StringInput): Expr<string> {
  return new Expr<string>(["downcase", r(input)]);
}

// ── Color ────────────────────────────────────────────────────

/** Create a color from RGB components (0-255). */
export function rgb(
  red: NumberInput,
  green: NumberInput,
  blue: NumberInput,
): Expr<ExprColor> {
  return new Expr<ExprColor>(["rgb", r(red), r(green), r(blue)]);
}

/** Create a color from RGBA components (RGB 0-255, alpha 0-1). */
export function rgba(
  red: NumberInput,
  green: NumberInput,
  blue: NumberInput,
  alpha: NumberInput,
): Expr<ExprColor> {
  return new Expr<ExprColor>(["rgba", r(red), r(green), r(blue), r(alpha)]);
}

/** Create a color from HSL components (hue 0-360, saturation/lightness 0-100%). */
export function hsl(
  hue: NumberInput,
  saturation: NumberInput,
  lightness: NumberInput,
): Expr<ExprColor> {
  return new Expr<ExprColor>(["hsl", r(hue), r(saturation), r(lightness)]);
}

/** Create a color from HSLA components (hue 0-360, saturation/lightness 0-100%, alpha 0-1). */
export function hsla(
  hue: NumberInput,
  saturation: NumberInput,
  lightness: NumberInput,
  alpha: NumberInput,
): Expr<ExprColor> {
  return new Expr<ExprColor>(["hsla", r(hue), r(saturation), r(lightness), r(alpha)]);
}

/** Decompose a color into its RGBA components as `[r, g, b, a]`. */
export function toRgba(color: ColorInput): Expr<number[]> {
  return new Expr<number[]>(["to-rgba", r(color)]);
}

// ── Interpolation methods ────────────────────────────────────

/** Linear interpolation method. */
export function linear(): unknown[] {
  return ["linear"];
}

/** Exponential interpolation method with the given base. */
export function exponential(base: number): unknown[] {
  return ["exponential", base];
}

/** Cubic bezier interpolation method. */
export function cubicBezier(
  x1: number,
  y1: number,
  x2: number,
  y2: number,
): unknown[] {
  return ["cubic-bezier", x1, y1, x2, y2];
}

// ── Ramps and scales ─────────────────────────────────────────

/**
 * Piecewise-constant function (staircase). Returns the output value of the stop
 * whose input value is just less than `input`.
 *
 * ```ts
 * expr.step(
 *   expr.get("population"),
 *   "#f7fbff",          // default (input < first stop)
 *   [10_000, "#6baed6"],
 *   [100_000, "#08306b"],
 * )
 * ```
 */
export function step(
  input: Resolvable,
  defaultOutput: Resolvable,
  ...stops: [number, Resolvable][]
): Expr<ExprValue> {
  const json: unknown[] = ["step", r(input), r(defaultOutput)];
  for (const [threshold, output] of stops) {
    json.push(threshold, r(output));
  }
  return new Expr<ExprValue>(json);
}

/**
 * Continuous interpolation between stops.
 *
 * ```ts
 * expr.interpolate(
 *   expr.linear(),
 *   expr.zoom(),
 *   [0, 0],
 *   [10, 1],
 *   [20, 5],
 * )
 * ```
 */
export function interpolate(
  method: unknown[],
  input: Resolvable,
  ...stops: [number, Resolvable][]
): Expr<ExprValue> {
  const json: unknown[] = ["interpolate", method, r(input)];
  for (const [stopInput, stopOutput] of stops) {
    json.push(stopInput, r(stopOutput));
  }
  return new Expr<ExprValue>(json);
}

/** Interpolate in the HCL color space. Produces smoother color gradients. */
export function interpolateHcl(
  method: unknown[],
  input: Resolvable,
  ...stops: [number, ColorInput][]
): Expr<ExprColor> {
  const json: unknown[] = ["interpolate-hcl", method, r(input)];
  for (const [stopInput, stopOutput] of stops) {
    json.push(stopInput, r(stopOutput));
  }
  return new Expr<ExprColor>(json);
}

/** Interpolate in the CIELAB color space. */
export function interpolateLab(
  method: unknown[],
  input: Resolvable,
  ...stops: [number, ColorInput][]
): Expr<ExprColor> {
  const json: unknown[] = ["interpolate-lab", method, r(input)];
  for (const [stopInput, stopOutput] of stops) {
    json.push(stopInput, r(stopOutput));
  }
  return new Expr<ExprColor>(json);
}

// ── Zoom ─────────────────────────────────────────────────────

/** The current map zoom level. In paint/layout, may only be the direct input to a top-level `step` or `interpolate`. */
export function zoom(): Expr<number> {
  return new Expr<number>(["zoom"]);
}

// ── Variable binding ─────────────────────────────────────────

/**
 * Bind variables for use in a result expression.
 *
 * ```ts
 * expr.let({ x: expr.get("population") },
 *   expr.multiply(expr.var("x"), 2),
 * )
 * ```
 */
export function letExpr(
  bindings: Record<string, Resolvable>,
  result: Resolvable,
): Expr<ExprValue> {
  const json: unknown[] = ["let"];
  for (const [name, value] of Object.entries(bindings)) {
    json.push(name, r(value));
  }
  json.push(r(result));
  return new Expr<ExprValue>(json);
}

/** Reference a variable bound by `let`. */
export function varExpr(name: string): Expr<ExprValue> {
  return new Expr<ExprValue>(["var", name]);
}

// ── Format and image ─────────────────────────────────────────

/** Options for a single segment in a `format()` expression. */
export interface FormatSegmentOptions {
  "font-scale"?: NumberInput;
  "text-font"?: Expr<unknown> | string[];
  "text-color"?: ColorInput;
}

/**
 * Create a formatted text value from one or more text segments with per-segment styling.
 *
 * Each segment is `[text, options?]`:
 * ```ts
 * expr.format(
 *   [expr.get("name"), { "font-scale": 1.2, "text-color": "#000" }],
 *   ["\n"],
 *   [expr.get("description")],
 * )
 * ```
 */
export function format(
  ...segments: [StringInput, FormatSegmentOptions?][]
): Expr<ExprFormatted> {
  const json: unknown[] = ["format"];
  for (const [text, opts] of segments) {
    json.push(r(text));
    const resolvedOpts: Record<string, unknown> = {};
    if (opts) {
      if (opts["font-scale"] !== undefined)
        resolvedOpts["font-scale"] = r(opts["font-scale"]);
      if (opts["text-font"] !== undefined)
        resolvedOpts["text-font"] =
          opts["text-font"] instanceof Expr
            ? opts["text-font"].toJSON()
            : opts["text-font"];
      if (opts["text-color"] !== undefined)
        resolvedOpts["text-color"] = r(opts["text-color"]);
    }
    json.push(resolvedOpts);
  }
  return new Expr<ExprFormatted>(json);
}

/** Options for `numberFormat()`. */
export interface NumberFormatOptions {
  locale?: StringInput;
  currency?: StringInput;
  "min-fraction-digits"?: NumberInput;
  "max-fraction-digits"?: NumberInput;
}

/** Format a number using locale-sensitive formatting. */
export function numberFormat(
  input: NumberInput,
  options?: NumberFormatOptions,
): Expr<string> {
  const opts: Record<string, unknown> = {};
  if (options) {
    if (options.locale !== undefined) opts.locale = r(options.locale);
    if (options.currency !== undefined) opts.currency = r(options.currency);
    if (options["min-fraction-digits"] !== undefined)
      opts["min-fraction-digits"] = r(options["min-fraction-digits"]);
    if (options["max-fraction-digits"] !== undefined)
      opts["max-fraction-digits"] = r(options["max-fraction-digits"]);
  }
  return new Expr<string>(["number-format", r(input), opts]);
}

/** Branded type for a collator value. */
export interface ExprCollator {
  readonly __brand: "honua-expr-collator";
}

/** Options for `collator()`. */
export interface CollatorOptions {
  "case-sensitive"?: BooleanInput;
  "diacritic-sensitive"?: BooleanInput;
  locale?: StringInput;
}

/** Create a collator for locale-aware string comparisons. */
export function collator(options?: CollatorOptions): Expr<ExprCollator> {
  const opts: Record<string, unknown> = {};
  if (options) {
    if (options["case-sensitive"] !== undefined)
      opts["case-sensitive"] = r(options["case-sensitive"]);
    if (options["diacritic-sensitive"] !== undefined)
      opts["diacritic-sensitive"] = r(options["diacritic-sensitive"]);
    if (options.locale !== undefined) opts.locale = r(options.locale);
  }
  return new Expr<ExprCollator>(["collator", opts]);
}

/** Get the resolved locale string from a collator. */
export function resolvedLocale(input: Expr<ExprCollator>): Expr<string> {
  return new Expr<string>(["resolved-locale", input.toJSON()]);
}

/** Resolve an image name from the style's sprite. Usable in `icon-image` and pattern properties. */
export function image(name: StringInput): Expr<ExprImage> {
  return new Expr<ExprImage>(["image", r(name)]);
}

// ── GeoJSON geometry type (minimal, avoids npm dependency) ───

export interface GeoJsonPoint {
  type: "Point";
  coordinates: number[];
}

export interface GeoJsonMultiPoint {
  type: "MultiPoint";
  coordinates: number[][];
}

export interface GeoJsonLineString {
  type: "LineString";
  coordinates: number[][];
}

export interface GeoJsonMultiLineString {
  type: "MultiLineString";
  coordinates: number[][][];
}

export interface GeoJsonPolygon {
  type: "Polygon";
  coordinates: number[][][];
}

export interface GeoJsonMultiPolygon {
  type: "MultiPolygon";
  coordinates: number[][][][];
}

export type GeoJsonGeometry =
  | GeoJsonPoint
  | GeoJsonMultiPoint
  | GeoJsonLineString
  | GeoJsonMultiLineString
  | GeoJsonPolygon
  | GeoJsonMultiPolygon;

// ── Spatial ──────────────────────────────────────────────────

function resolveGeometry(input: GeoJsonGeometry | Expr<ExprValue>): unknown {
  return input instanceof Expr ? input.toJSON() : input;
}

/** Shortest distance in meters from the evaluated feature to the input geometry. */
export function distance(geometry: GeoJsonGeometry | Expr<ExprValue>): Expr<number> {
  return new Expr<number>(["distance", resolveGeometry(geometry)]);
}

/** True if the evaluated feature is entirely within the input geometry. */
export function within(geometry: GeoJsonGeometry | Expr<ExprValue>): Expr<boolean> {
  return new Expr<boolean>(["within", resolveGeometry(geometry)]);
}

/** True if the evaluated feature intersects the input geometry. */
export function intersects(geometry: GeoJsonGeometry | Expr<ExprValue>): Expr<boolean> {
  return new Expr<boolean>(["intersects", resolveGeometry(geometry)]);
}

// ── Namespace export ─────────────────────────────────────────

/**
 * Namespace object that groups all expression builder functions.
 *
 * @example
 * ```ts
 * import { expr } from "@honua/sdk";
 *
 * const fillColor = expr.step(
 *   expr.get("assessed_value"),
 *   "#f7fbff",
 *   [100_000, "#6baed6"],
 *   [500_000, "#08306b"],
 * );
 *
 * map.addLayer({
 *   id: "parcel-fill",
 *   source: "parcels",
 *   type: "fill",
 *   paint: { "fill-color": fillColor.toJSON() },
 * });
 * ```
 */
export const expr = {
  // Lookup
  get,
  has,
  at,
  contains,
  indexOf,
  slice,
  length,

  // Feature data
  id,
  geometryType,
  properties,
  featureState,

  // Type coercion
  literal,
  toBoolean,
  toNumber,
  toString: exprToString,
  toColor,
  typeOf,

  // Comparison
  eq,
  neq,
  lt,
  lte,
  gt,
  gte,

  // Logic
  not,
  all,
  any,

  // Decision
  case: switchCase,
  match: matchExpr,
  coalesce,

  // Math
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
  ln2: ln2Const,

  // String
  concat,
  upcase,
  downcase,

  // Color
  rgb,
  rgba,
  hsl,
  hsla,
  toRgba,

  // Interpolation
  step,
  interpolate,
  interpolateHcl,
  interpolateLab,
  linear,
  exponential,
  cubicBezier,

  // Zoom
  zoom,

  // Variable binding
  let: letExpr,
  var: varExpr,

  // Format/image
  format,
  numberFormat,
  collator,
  resolvedLocale,
  image,

  // Spatial
  distance,
  within,
  intersects,
} as const;
