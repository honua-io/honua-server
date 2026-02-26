import {
  isKindSupportedForTarget,
  SUPPORTED_ARCGIS_MODULE_KIND_BY_PATH,
  type CodemodConstructorKind,
} from "./codemod.js";

export type JsParityCategory = "layer" | "view" | "widget" | "control";
export type JsParityStatus = "native" | "compat" | "assisted" | "unsupported";
export type JsParityMatrixKind =
  | CodemodConstructorKind
  | "track-widget"
  | "directions-widget"
  | "route-layer";

export interface JsParityMatrixEntry {
  kind: JsParityMatrixKind;
  category: JsParityCategory;
  arcGisModule: string;
  honuaCompat: JsParityStatus;
  esriLeaflet: JsParityStatus;
  notes: string;
}

export interface JsParitySummary {
  honuaCompat: Record<JsParityStatus, number>;
  esriLeaflet: Record<JsParityStatus, number>;
}

const CONTROL_KINDS = new Set<CodemodConstructorKind>([
  "home-widget",
  "basemap-toggle-widget",
  "locate-widget",
  "scale-bar-widget",
  "compass-widget",
  "fullscreen-widget",
  "zoom-widget",
  "attribution-widget",
]);

const VIEW_KINDS = new Set<CodemodConstructorKind>(["map", "map-view", "scene-view", "web-map"]);

const LAYER_KINDS = new Set<CodemodConstructorKind>([
  "feature-layer",
  "graphics-layer",
  "group-layer",
  "map-image-layer",
  "tile-layer",
]);

const CANONICAL_MODULE_BY_KIND = buildCanonicalModuleMap();

const BASE_MATRIX_ROWS: JsParityMatrixEntry[] = (
  Object.keys(CANONICAL_MODULE_BY_KIND) as CodemodConstructorKind[]
)
  .sort()
  .map((kind) => {
    const honuaCompat: JsParityStatus = isKindSupportedForTarget(kind, "honua-compat")
      ? "compat"
      : "unsupported";
    const esriLeaflet: JsParityStatus = isKindSupportedForTarget(kind, "esri-leaflet")
      ? "compat"
      : "assisted";
    return {
      kind,
      category: classifyCategory(kind),
      arcGisModule: CANONICAL_MODULE_BY_KIND[kind],
      honuaCompat,
      esriLeaflet,
      notes:
        esriLeaflet === "compat"
          ? "deterministic codemod mapping available"
          : "assisted migration with TODO/report gating",
    };
  });

const EXTRA_MATRIX_ROWS: readonly JsParityMatrixEntry[] = [
  {
    kind: "directions-widget",
    category: "widget",
    arcGisModule: "@arcgis/core/widgets/Directions",
    honuaCompat: "unsupported",
    esriLeaflet: "unsupported",
    notes: "requires routing service and UI workflow design",
  },
  {
    kind: "route-layer",
    category: "layer",
    arcGisModule: "@arcgis/core/layers/RouteLayer",
    honuaCompat: "unsupported",
    esriLeaflet: "unsupported",
    notes: "requires route network service parity",
  },
];

export const JS_PARITY_MATRIX: readonly JsParityMatrixEntry[] = Object.freeze([
  ...BASE_MATRIX_ROWS,
  ...EXTRA_MATRIX_ROWS,
]);

export function getJsParityMatrix(): readonly JsParityMatrixEntry[] {
  return JS_PARITY_MATRIX;
}

export function summarizeJsParityMatrix(
  matrix: readonly JsParityMatrixEntry[] = JS_PARITY_MATRIX,
): JsParitySummary {
  const summary: JsParitySummary = {
    honuaCompat: {
      native: 0,
      compat: 0,
      assisted: 0,
      unsupported: 0,
    },
    esriLeaflet: {
      native: 0,
      compat: 0,
      assisted: 0,
      unsupported: 0,
    },
  };

  for (const row of matrix) {
    summary.honuaCompat[row.honuaCompat] += 1;
    summary.esriLeaflet[row.esriLeaflet] += 1;
  }

  return summary;
}

function buildCanonicalModuleMap(): Record<CodemodConstructorKind, string> {
  const result = {} as Record<CodemodConstructorKind, string>;

  for (const [modulePath, kind] of Object.entries(SUPPORTED_ARCGIS_MODULE_KIND_BY_PATH)) {
    if (modulePath.endsWith(".js")) {
      continue;
    }
    if (result[kind] === undefined) {
      result[kind] = modulePath;
    }
  }

  return result;
}

function classifyCategory(kind: CodemodConstructorKind): JsParityCategory {
  if (LAYER_KINDS.has(kind)) {
    return "layer";
  }
  if (VIEW_KINDS.has(kind)) {
    return "view";
  }
  if (CONTROL_KINDS.has(kind)) {
    return "control";
  }
  return "widget";
}
