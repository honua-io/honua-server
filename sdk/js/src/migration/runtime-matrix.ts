export type JsRuntimeParitySurface =
  | "feature-layer"
  | "map-image-layer"
  | "map-view"
  | "widget"
  | "control";

export type JsRuntimeParityStatus = "native" | "compat" | "assisted" | "unsupported";

export interface JsRuntimeParityEntry {
  surface: JsRuntimeParitySurface;
  capability: string;
  arcGisJsApi: string;
  honuaCompat: JsRuntimeParityStatus;
  esriLeaflet: JsRuntimeParityStatus;
  notes: string;
}

export interface JsRuntimeParitySummary {
  honuaCompat: Record<JsRuntimeParityStatus, number>;
  esriLeaflet: Record<JsRuntimeParityStatus, number>;
}

const BASE_RUNTIME_MATRIX: readonly JsRuntimeParityEntry[] = Object.freeze([
  {
    surface: "feature-layer",
    capability: "query-features",
    arcGisJsApi: "FeatureLayer.queryFeatures",
    honuaCompat: "compat",
    esriLeaflet: "compat",
    notes: "Feature query flow mapped in compat and esri-leaflet targets.",
  },
  {
    surface: "feature-layer",
    capability: "query-object-ids",
    arcGisJsApi: "FeatureLayer.queryObjectIds",
    honuaCompat: "compat",
    esriLeaflet: "assisted",
    notes: "ObjectId query helper is implemented in compat; esri-leaflet requires adapter logic.",
  },
  {
    surface: "feature-layer",
    capability: "query-feature-count",
    arcGisJsApi: "FeatureLayer.queryFeatureCount",
    honuaCompat: "compat",
    esriLeaflet: "assisted",
    notes: "Count query helper is implemented in compat; esri-leaflet requires manual bridge.",
  },
  {
    surface: "feature-layer",
    capability: "query-extent",
    arcGisJsApi: "FeatureLayer.queryExtent",
    honuaCompat: "compat",
    esriLeaflet: "assisted",
    notes: "Extent query helper is implemented in compat.",
  },
  {
    surface: "feature-layer",
    capability: "query-related-features",
    arcGisJsApi: "FeatureLayer.queryRelatedFeatures",
    honuaCompat: "compat",
    esriLeaflet: "assisted",
    notes: "Related-records helper is compat-only.",
  },
  {
    surface: "feature-layer",
    capability: "apply-edits",
    arcGisJsApi: "FeatureLayer.applyEdits",
    honuaCompat: "compat",
    esriLeaflet: "assisted",
    notes: "Edit helper is available in compat for migration-critical edits.",
  },
  {
    surface: "feature-layer",
    capability: "attachments-query-list-delete",
    arcGisJsApi: "FeatureLayer.queryAttachments/listAttachments/deleteAttachments",
    honuaCompat: "compat",
    esriLeaflet: "assisted",
    notes: "Attachment read/delete helpers are compat-ready.",
  },
  {
    surface: "feature-layer",
    capability: "attachments-add-update",
    arcGisJsApi: "FeatureLayer.addAttachment/updateAttachment",
    honuaCompat: "compat",
    esriLeaflet: "assisted",
    notes: "Multipart attachment upload/update helpers are compat-ready.",
  },
  {
    surface: "feature-layer",
    capability: "schema-field-lookups",
    arcGisJsApi: "FeatureLayer.getField/fields",
    honuaCompat: "compat",
    esriLeaflet: "assisted",
    notes: "Field schema helpers are available in compat.",
  },
  {
    surface: "map-image-layer",
    capability: "export-image",
    arcGisJsApi: "MapImageLayer.exportImage",
    honuaCompat: "compat",
    esriLeaflet: "assisted",
    notes: "Map export helper is mapped in compat runtime.",
  },
  {
    surface: "map-image-layer",
    capability: "legend",
    arcGisJsApi: "MapImageLayer.getLegend/legend",
    honuaCompat: "compat",
    esriLeaflet: "assisted",
    notes: "Legend helper is mapped in compat runtime.",
  },
  {
    surface: "map-image-layer",
    capability: "find",
    arcGisJsApi: "MapImageLayer.find",
    honuaCompat: "compat",
    esriLeaflet: "assisted",
    notes: "MapServer find helper is mapped in compat runtime.",
  },
  {
    surface: "map-image-layer",
    capability: "identify",
    arcGisJsApi: "MapImageLayer.identify",
    honuaCompat: "compat",
    esriLeaflet: "assisted",
    notes: "Identify helper is mapped in compat runtime.",
  },
  {
    surface: "map-image-layer",
    capability: "sublayer-lookup",
    arcGisJsApi: "MapImageLayer.findSublayerById/allSublayers",
    honuaCompat: "compat",
    esriLeaflet: "assisted",
    notes: "Sublayer lookup helpers are available in compat runtime.",
  },
  {
    surface: "map-view",
    capability: "navigation-go-to",
    arcGisJsApi: "MapView.goTo",
    honuaCompat: "compat",
    esriLeaflet: "compat",
    notes: "goTo normalization is available through deterministic esri-leaflet target fallback to MapViewCompat.",
  },
  {
    surface: "map-view",
    capability: "hit-test",
    arcGisJsApi: "MapView.hitTest",
    honuaCompat: "compat",
    esriLeaflet: "compat",
    notes: "Popup-backed hitTest helper is available through deterministic esri-leaflet target fallback to MapViewCompat.",
  },
  {
    surface: "map-view",
    capability: "popup-bridge",
    arcGisJsApi: "MapView.popup/openPopup/closePopup",
    honuaCompat: "compat",
    esriLeaflet: "compat",
    notes: "Popup bridge helpers are available through deterministic esri-leaflet target fallback to MapViewCompat.",
  },
  {
    surface: "map-view",
    capability: "ui-components",
    arcGisJsApi: "MapView.ui.add/remove/move/getComponents",
    honuaCompat: "compat",
    esriLeaflet: "compat",
    notes: "UI component container APIs are available through deterministic esri-leaflet target fallback to MapViewCompat.",
  },
  {
    surface: "map-view",
    capability: "layer-view-queries",
    arcGisJsApi: "MapView.whenLayerView + layerView.query*",
    honuaCompat: "compat",
    esriLeaflet: "compat",
    notes: "Layer-view creation, querying, and highlight handles are compat-ready through deterministic esri-leaflet target fallback to MapViewCompat.",
  },
  {
    surface: "widget",
    capability: "layer-list-and-legend",
    arcGisJsApi: "LayerList/Legend",
    honuaCompat: "compat",
    esriLeaflet: "compat",
    notes: "LayerList/Legend are deterministic in esri-leaflet target via compat fallback wrappers and shared event bus integration.",
  },
  {
    surface: "widget",
    capability: "popup-and-search",
    arcGisJsApi: "Popup/Search",
    honuaCompat: "compat",
    esriLeaflet: "compat",
    notes: "Popup/Search are deterministic in esri-leaflet target via compat fallback wrappers and shared event bus integration.",
  },
  {
    surface: "widget",
    capability: "navigation-widgets",
    arcGisJsApi: "BasemapGallery/Bookmarks/Expand",
    honuaCompat: "compat",
    esriLeaflet: "compat",
    notes: "Navigation widgets are deterministic in esri-leaflet target via compat fallback wrappers.",
  },
  {
    surface: "widget",
    capability: "editing-and-analysis",
    arcGisJsApi: "Sketch/Editor/Track/Measurement/TimeSlider/Directions",
    honuaCompat: "compat",
    esriLeaflet: "assisted",
    notes: "Common editing and analysis widgets are available in compat runtime.",
  },
  {
    surface: "control",
    capability: "common-map-controls",
    arcGisJsApi: "Home/BasemapToggle/Locate/ScaleBar/Compass/Fullscreen/Zoom/Attribution",
    honuaCompat: "compat",
    esriLeaflet: "compat",
    notes: "Common map controls are deterministic in esri-leaflet target via compat fallback wrappers and event-bus integration.",
  },
]);

export const JS_RUNTIME_PARITY_MATRIX: readonly JsRuntimeParityEntry[] =
  Object.freeze([...BASE_RUNTIME_MATRIX]);

export function getJsRuntimeParityMatrix(): readonly JsRuntimeParityEntry[] {
  return JS_RUNTIME_PARITY_MATRIX;
}

export function summarizeJsRuntimeParity(
  matrix: readonly JsRuntimeParityEntry[] = JS_RUNTIME_PARITY_MATRIX,
): JsRuntimeParitySummary {
  const summary: JsRuntimeParitySummary = {
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
