/**
 * HonuaMapSpec — Honua-specific source type definitions extending MapLibre Style Spec v8.
 *
 * Defines three custom source types (`honua-feature-service`, `honua-map-service`,
 * `honua-ogc-features`) and a `HonuaStyleSpecification` that widens the MapLibre
 * style's `sources` map to accept them. Standard MapLibre layer types, paint/layout
 * properties, and expressions are used unchanged.
 *
 * These types are standalone (zero dependency on `maplibre-gl` or
 * `@maplibre/maplibre-gl-style-spec`), but structurally compatible — a
 * `HonuaStyleSpecification` can be narrowed to `StyleSpecification` by filtering
 * out Honua sources.
 *
 * @module
 */

// ── Honua source types ───────────────────────────────────────

/** Common fields shared by all Honua source specifications. */
export interface HonuaSourceBase {
  /** Full URL to the service endpoint. */
  url: string;
  /** Attribution string displayed on the map. */
  attribution?: string;
}

/**
 * A source backed by an Esri-compatible Feature Service layer.
 *
 * URL format: `https://host/rest/services/{serviceId}/FeatureServer/{layerId}`
 */
export interface HonuaFeatureServiceSourceSpecification extends HonuaSourceBase {
  type: "honua-feature-service";
  /** Server-side WHERE clause applied to all queries. */
  definitionExpression?: string;
  /** Fields to include in query responses. Defaults to all (`["*"]`). */
  outFields?: string[];
  /** Whether queries return geometry. Defaults to `true`. */
  returnGeometry?: boolean;
  /** Output spatial reference WKID (e.g. `4326`, `3857`). */
  outSR?: number;
}

/**
 * A source backed by an Esri-compatible Map Service (dynamic map export).
 *
 * URL format: `https://host/rest/services/{serviceId}/MapServer`
 */
export interface HonuaMapServiceSourceSpecification extends HonuaSourceBase {
  type: "honua-map-service";
  /** Comma-separated layer IDs to include (e.g. `"0,1,3"`). */
  layers?: string;
  /** Export DPI. Defaults to `96`. */
  dpi?: number;
  /** Image format. */
  format?: "png" | "png24" | "png32" | "jpg" | "gif";
  /** Whether the exported image has a transparent background. */
  transparent?: boolean;
}

/**
 * A source backed by an OGC API Features collection.
 *
 * URL format: `https://host/ogc/collections/{collectionId}` or
 * `https://host/ogc` (with `collectionId` specified separately).
 */
export interface HonuaOgcFeaturesSourceSpecification extends HonuaSourceBase {
  type: "honua-ogc-features";
  /** The OGC collection identifier. Required if the URL does not include it. */
  collectionId?: string;
  /** Coordinate reference system URI (e.g. `"http://www.opengis.net/def/crs/OGC/1.3/CRS84"`). */
  crs?: string;
  /** CQL2 filter expression applied server-side. */
  filter?: string;
  /** Maximum items per request page. */
  limit?: number;
}

/** Union of all Honua custom source types. */
export type HonuaSourceSpecification =
  | HonuaFeatureServiceSourceSpecification
  | HonuaMapServiceSourceSpecification
  | HonuaOgcFeaturesSourceSpecification;

// ── Minimal layer type (MapLibre-compatible) ─────────────────

/** Minimal layer shape compatible with MapLibre Style Spec v8. */
export interface HonuaLayerSpecification {
  id: string;
  type: string;
  source?: string;
  "source-layer"?: string;
  filter?: unknown;
  layout?: Record<string, unknown>;
  paint?: Record<string, unknown>;
  minzoom?: number;
  maxzoom?: number;
  metadata?: Record<string, unknown>;
}

// ── Style specification ──────────────────────────────────────

/**
 * A MapLibre Style Spec v8 document extended with Honua custom source types.
 *
 * Standard MapLibre sources (`vector`, `raster`, `geojson`, etc.) are accepted
 * alongside Honua sources. Layer definitions use standard MapLibre types.
 */
export interface HonuaStyleSpecification {
  version: 8;
  name?: string;
  metadata?: Record<string, unknown>;
  center?: [number, number];
  zoom?: number;
  bearing?: number;
  pitch?: number;
  sources: Record<string, HonuaSourceSpecification | { type: string; [key: string]: unknown }>;
  layers: HonuaLayerSpecification[];
  sprite?: string | { id: string; url: string }[];
  glyphs?: string;
  transition?: { duration?: number; delay?: number };
}

// ── Type guards ──────────────────────────────────────────────

/** Test whether a source specification is any Honua custom type. */
export function isHonuaSource(source: { type: string }): source is HonuaSourceSpecification {
  return source.type.startsWith("honua-");
}

/** Test whether a source is a `honua-feature-service`. */
export function isFeatureServiceSource(
  source: { type: string },
): source is HonuaFeatureServiceSourceSpecification {
  return source.type === "honua-feature-service";
}

/** Test whether a source is a `honua-map-service`. */
export function isMapServiceSource(
  source: { type: string },
): source is HonuaMapServiceSourceSpecification {
  return source.type === "honua-map-service";
}

/** Test whether a source is a `honua-ogc-features`. */
export function isOgcFeaturesSource(
  source: { type: string },
): source is HonuaOgcFeaturesSourceSpecification {
  return source.type === "honua-ogc-features";
}

// ── URL parsing ──────────────────────────────────────────────

// Feature Service and Map Service URL parsers are re-exported from
// esri-compat/url.ts (parseFeatureLayerUrl, parseMapServiceUrl) to avoid
// duplication. Only the OGC Features parser is defined here since it has
// no esri-compat equivalent.

/** Parsed components of an OGC Features URL. */
export interface ParsedOgcFeaturesUrl {
  baseUrl: string;
  collectionId: string | undefined;
}

/**
 * Parse an OGC Features URL into baseUrl and optional collectionId.
 *
 * Accepts URLs like:
 * - `https://gis.example.com/ogc/collections/admin-boundaries`
 * - `https://gis.example.com/ogc` (collectionId provided separately)
 */
export function parseOgcFeaturesUrl(url: string): ParsedOgcFeaturesUrl {
  const collectionsMatch = url.match(
    /^(https?:\/\/[^/]+(?:\/[^/]+)*?)\/collections\/([^/?#]+)/i,
  );
  if (collectionsMatch) {
    return {
      baseUrl: collectionsMatch[1],
      collectionId: collectionsMatch[2],
    };
  }
  return {
    baseUrl: url.replace(/\/+$/, ""),
    collectionId: undefined,
  };
}

// ── Style validation ─────────────────────────────────────────

/** A validation error found in a HonuaStyleSpecification. */
export interface StyleValidationError {
  path: string;
  message: string;
}

/**
 * Validate a HonuaStyleSpecification for structural correctness.
 *
 * Returns an empty array if the style is valid. Does not validate MapLibre
 * layer/expression semantics — use `@maplibre/maplibre-gl-style-spec` for that.
 */
export function validateHonuaStyle(style: unknown): StyleValidationError[] {
  const errors: StyleValidationError[] = [];

  if (typeof style !== "object" || style === null) {
    errors.push({ path: "", message: "Style must be a non-null object" });
    return errors;
  }

  const s = style as Record<string, unknown>;

  if (s.version !== 8) {
    errors.push({ path: "version", message: "Version must be 8" });
  }

  if (typeof s.sources !== "object" || s.sources === null || Array.isArray(s.sources)) {
    errors.push({ path: "sources", message: "Sources must be a non-null object" });
  } else {
    for (const [name, source] of Object.entries(s.sources as Record<string, unknown>)) {
      if (typeof source !== "object" || source === null) {
        errors.push({ path: `sources.${name}`, message: "Source must be a non-null object" });
        continue;
      }
      const src = source as Record<string, unknown>;
      if (typeof src.type !== "string") {
        errors.push({ path: `sources.${name}.type`, message: "Source type must be a string" });
        continue;
      }
      if (isHonuaSource(src as { type: string })) {
        if (typeof (src as { url?: unknown }).url !== "string") {
          errors.push({ path: `sources.${name}.url`, message: "Honua source must have a string url" });
        }
      }
    }
  }

  if (!Array.isArray(s.layers)) {
    errors.push({ path: "layers", message: "Layers must be an array" });
  } else {
    for (let i = 0; i < (s.layers as unknown[]).length; i++) {
      const layer = (s.layers as unknown[])[i];
      if (typeof layer !== "object" || layer === null) {
        errors.push({ path: `layers[${i}]`, message: "Layer must be a non-null object" });
        continue;
      }
      const l = layer as Record<string, unknown>;
      if (typeof l.id !== "string") {
        errors.push({ path: `layers[${i}].id`, message: "Layer must have a string id" });
      }
      if (typeof l.type !== "string") {
        errors.push({ path: `layers[${i}].type`, message: "Layer must have a string type" });
      }
    }
  }

  return errors;
}
