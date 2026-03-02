export type JsRuntimeParitySurface = "feature-layer" | "map-image-layer" | "map-view" | "widget" | "control";

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
    esriLeaflet: "compat",
    notes: "ObjectId query helper is available via esri-leaflet target compat fallback for method-aware layer usage.",
  },
  {
    surface: "feature-layer",
    capability: "query-feature-count",
    arcGisJsApi: "FeatureLayer.queryFeatureCount",
    honuaCompat: "compat",
    esriLeaflet: "compat",
    notes: "Count query helper is available via esri-leaflet target compat fallback for method-aware layer usage.",
  },
  {
    surface: "feature-layer",
    capability: "query-extent",
    arcGisJsApi: "FeatureLayer.queryExtent",
    honuaCompat: "compat",
    esriLeaflet: "compat",
    notes: "Extent query helper is available via esri-leaflet target compat fallback for method-aware layer usage.",
  },
  {
    surface: "feature-layer",
    capability: "query-related-features",
    arcGisJsApi: "FeatureLayer.queryRelatedFeatures",
    honuaCompat: "compat",
    esriLeaflet: "compat",
    notes: "Related-records helper is available via esri-leaflet target compat fallback for method-aware layer usage.",
  },
  {
    surface: "feature-layer",
    capability: "apply-edits",
    arcGisJsApi: "FeatureLayer.applyEdits",
    honuaCompat: "compat",
    esriLeaflet: "compat",
    notes: "Edit helper is available via esri-leaflet target compat fallback for method-aware layer usage.",
  },
  {
    surface: "feature-layer",
    capability: "attachments-query-list-delete",
    arcGisJsApi: "FeatureLayer.queryAttachments/listAttachments/deleteAttachments",
    honuaCompat: "compat",
    esriLeaflet: "compat",
    notes:
      "Attachment read/delete helpers are available via esri-leaflet target compat fallback for method-aware layer usage.",
  },
  {
    surface: "feature-layer",
    capability: "attachments-add-update",
    arcGisJsApi: "FeatureLayer.addAttachment/updateAttachment",
    honuaCompat: "compat",
    esriLeaflet: "compat",
    notes:
      "Multipart attachment upload/update helpers are available via esri-leaflet target compat fallback for method-aware layer usage.",
  },
  {
    surface: "feature-layer",
    capability: "schema-field-lookups",
    arcGisJsApi: "FeatureLayer.getField/fields",
    honuaCompat: "compat",
    esriLeaflet: "compat",
    notes: "Field schema helpers are available via esri-leaflet target compat fallback for method-aware layer usage.",
  },
  {
    surface: "map-image-layer",
    capability: "export-image",
    arcGisJsApi: "MapImageLayer.exportImage",
    honuaCompat: "compat",
    esriLeaflet: "compat",
    notes: "Map export helper is available via esri-leaflet target compat fallback for method-aware layer usage.",
  },
  {
    surface: "map-image-layer",
    capability: "legend",
    arcGisJsApi: "MapImageLayer.getLegend/legend",
    honuaCompat: "compat",
    esriLeaflet: "compat",
    notes: "Legend helper is available via esri-leaflet target compat fallback for method-aware layer usage.",
  },
  {
    surface: "map-image-layer",
    capability: "find",
    arcGisJsApi: "MapImageLayer.find",
    honuaCompat: "compat",
    esriLeaflet: "compat",
    notes: "MapServer find helper is available via esri-leaflet target compat fallback for method-aware layer usage.",
  },
  {
    surface: "map-image-layer",
    capability: "identify",
    arcGisJsApi: "MapImageLayer.identify",
    honuaCompat: "compat",
    esriLeaflet: "compat",
    notes: "Identify helper is available via esri-leaflet target compat fallback for method-aware layer usage.",
  },
  {
    surface: "map-image-layer",
    capability: "query-features",
    arcGisJsApi: "MapImageLayer/Sublayer.queryFeatures",
    honuaCompat: "compat",
    esriLeaflet: "compat",
    notes: "MapServer layer query helper is available via compat query bridge on MapImageLayer wrappers.",
  },
  {
    surface: "map-image-layer",
    capability: "query-feature-count",
    arcGisJsApi: "MapImageLayer/Sublayer.queryFeatureCount",
    honuaCompat: "compat",
    esriLeaflet: "compat",
    notes: "MapServer layer count helper is available through query bridge wrappers on MapImageLayer.",
  },
  {
    surface: "map-image-layer",
    capability: "query-object-ids",
    arcGisJsApi: "MapImageLayer/Sublayer.queryObjectIds",
    honuaCompat: "compat",
    esriLeaflet: "compat",
    notes: "MapServer layer objectId helper is available through query bridge wrappers on MapImageLayer.",
  },
  {
    surface: "map-image-layer",
    capability: "query-extent",
    arcGisJsApi: "MapImageLayer/Sublayer.queryExtent",
    honuaCompat: "compat",
    esriLeaflet: "compat",
    notes: "MapServer layer extent helper is available through query bridge wrappers on MapImageLayer.",
  },
  {
    surface: "map-image-layer",
    capability: "query-related-features",
    arcGisJsApi: "MapImageLayer/Sublayer.queryRelatedFeatures",
    honuaCompat: "compat",
    esriLeaflet: "compat",
    notes: "MapServer related-record query helper is available through query bridge wrappers on MapImageLayer.",
  },
  {
    surface: "map-image-layer",
    capability: "sublayer-lookup",
    arcGisJsApi: "MapImageLayer.findSublayerById/allSublayers",
    honuaCompat: "compat",
    esriLeaflet: "compat",
    notes:
      "Sublayer lookup helpers are available via esri-leaflet target compat fallback for method-aware layer usage.",
  },
  {
    surface: "map-image-layer",
    capability: "sublayer-query-wrapper",
    arcGisJsApi:
      "MapImageLayer.sublayer(id).queryFeatures/queryFeatureCount/queryObjectIds/queryExtent/queryRelatedFeatures",
    honuaCompat: "compat",
    esriLeaflet: "compat",
    notes: "Sublayer query wrapper is available through compat bridges for MapServer sublayer query ergonomics.",
  },
  {
    surface: "map-image-layer",
    capability: "sublayer-visibility-and-filters",
    arcGisJsApi: "MapImageLayer.sublayer(id).visible/definitionExpression",
    honuaCompat: "compat",
    esriLeaflet: "compat",
    notes:
      "Sublayer visibility and definition-expression bridges are available on compat sublayer wrappers for TOC/query parity.",
  },
  {
    surface: "feature-layer",
    capability: "event-lifecycle",
    arcGisJsApi: "FeatureLayer.on(edits/refresh/layerview-create)",
    honuaCompat: "compat",
    esriLeaflet: "compat",
    notes: "Typed .on() method with event bus bridge for edits, refresh, and layerview-create events.",
  },
  {
    surface: "feature-layer",
    capability: "time-extent-filtering",
    arcGisJsApi: "FeatureLayer.timeExtent / TimeSlider integration",
    honuaCompat: "compat",
    esriLeaflet: "compat",
    notes:
      "TimeExtent property auto-appends time parameter to queryFeatures; TimeSliderCompat.connectLayer() for declarative binding.",
  },
  {
    surface: "feature-layer",
    capability: "streaming-pagination",
    arcGisJsApi: "FeatureLayer.queryFeaturesAll / queryFeaturesStream",
    honuaCompat: "compat",
    esriLeaflet: "compat",
    notes: "Streaming async-generator pagination and typed query parameters for large result sets.",
  },
  {
    surface: "feature-layer",
    capability: "popup-template-interpolation",
    arcGisJsApi: "PopupTemplate.getTitle/getContent with field expressions",
    honuaCompat: "compat",
    esriLeaflet: "compat",
    notes: "PopupTemplate field expression interpolation and function-based title/content support.",
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
    notes:
      "Popup-backed hitTest helper is available through deterministic esri-leaflet target fallback to MapViewCompat.",
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
    notes:
      "UI component container APIs are available through deterministic esri-leaflet target fallback to MapViewCompat.",
  },
  {
    surface: "map-view",
    capability: "layer-view-queries",
    arcGisJsApi: "MapView.whenLayerView + layerView.query*",
    honuaCompat: "compat",
    esriLeaflet: "compat",
    notes:
      "Layer-view creation, querying, and highlight handles are compat-ready through deterministic esri-leaflet target fallback to MapViewCompat.",
  },
  {
    surface: "widget",
    capability: "layer-list-and-legend",
    arcGisJsApi: "LayerList/Legend",
    honuaCompat: "compat",
    esriLeaflet: "compat",
    notes:
      "LayerList/Legend are deterministic in esri-leaflet target via compat fallback wrappers and shared event bus integration.",
  },
  {
    surface: "widget",
    capability: "popup-and-search",
    arcGisJsApi: "Popup/Search",
    honuaCompat: "compat",
    esriLeaflet: "compat",
    notes:
      "Popup/Search are deterministic in esri-leaflet target via compat fallback wrappers and shared event bus integration.",
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
    esriLeaflet: "compat",
    notes: "Common editing and analysis widgets are deterministic in esri-leaflet target via compat fallback wrappers.",
  },
  {
    surface: "widget",
    capability: "search-expanded-options",
    arcGisJsApi: "Search.searchAllEnabled/popupEnabled/maxResults/allPlaceholder/locationEnabled/resultGraphicEnabled",
    honuaCompat: "compat",
    esriLeaflet: "compat",
    notes: "Expanded Search widget options for codemod auto-migration coverage.",
  },
  {
    surface: "widget",
    capability: "feature-table-expanded-options",
    arcGisJsApi: "FeatureTable.selectionMode/rowSelectionEnabled/highlightEnabled/pageSize/autoRefreshEnabled",
    honuaCompat: "compat",
    esriLeaflet: "compat",
    notes: "Expanded FeatureTable widget options for codemod auto-migration coverage.",
  },
  {
    surface: "control",
    capability: "common-map-controls",
    arcGisJsApi: "Home/BasemapToggle/Locate/ScaleBar/Compass/Fullscreen/Zoom/Attribution",
    honuaCompat: "compat",
    esriLeaflet: "compat",
    notes:
      "Common map controls are deterministic in esri-leaflet target via compat fallback wrappers and event-bus integration.",
  },
]);

export const JS_RUNTIME_PARITY_MATRIX: readonly JsRuntimeParityEntry[] = Object.freeze([...BASE_RUNTIME_MATRIX]);

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
