export { HonuaClient } from "./core/client.js";
export { HonuaHttpError } from "./core/errors.js";
export type {
  ApplyEditsRequest,
  ExportMapRequest,
  HonuaClientOptions,
  HonuaRawRequest,
  HonuaErrorContext,
  HonuaRequestContext,
  HonuaRequestInterceptor,
  HonuaRequestMutation,
  HonuaResponseContext,
  MapIdentifyRequest,
  MapLegendRequest,
  QueryFeaturesRequest,
  QueryMethod,
  QueryRelatedRecordsRequest,
} from "./core/types.js";

export { FeatureLayerCompat } from "./esri-compat/feature-layer.js";
export type {
  FeatureLayerCreateQueryResult,
  FeatureLayerDeleteAttachmentsOptions,
  FeatureLayerListAttachmentsOptions,
  FeatureLayerQueryAttachmentsOptions,
  FeatureLayerQueryCountOptions,
} from "./esri-compat/feature-layer.js";
export { FeatureCompat } from "./esri-compat/feature.js";
export type { FeatureCompatOptions } from "./esri-compat/feature.js";
export { FeatureFormCompat } from "./esri-compat/feature-form.js";
export type { FeatureFormCompatOptions, FeatureFormSubmitResultCompat } from "./esri-compat/feature-form.js";
export { FeatureTemplatesCompat } from "./esri-compat/feature-templates.js";
export type {
  FeatureTemplateItemCompat,
  FeatureTemplatesCompatOptions,
} from "./esri-compat/feature-templates.js";
export { FeatureTableCompat } from "./esri-compat/feature-table.js";
export type { FeatureTableCompatOptions, FeatureTableRowCompat } from "./esri-compat/feature-table.js";
export { CompatEventBus } from "./esri-compat/event-bus.js";
export type {
  CompatEvent,
  CompatEventListener,
  CompatEventSubscription,
} from "./esri-compat/event-bus.js";
export {
  AttributionCompat,
  BasemapToggleCompat,
  CompassCompat,
  FullscreenCompat,
  HomeCompat,
  LocateCompat,
  ScaleBarCompat,
  ZoomCompat,
} from "./esri-compat/controls.js";
export type {
  AttributionCompatOptions,
  BasemapToggleCompatOptions,
  CompassCompatOptions,
  FullscreenCompatOptions,
  HomeCompatOptions,
  HomeViewpointCompat,
  LocateCompatOptions,
  LocatePositionCompat,
  ScaleBarCompatOptions,
  ScaleBarUnitCompat,
  ZoomCompatOptions,
} from "./esri-compat/controls.js";
export { BasemapGalleryCompat } from "./esri-compat/basemap-gallery.js";
export type { BasemapGalleryCompatOptions } from "./esri-compat/basemap-gallery.js";
export { BasemapLayerListCompat } from "./esri-compat/basemap-layer-list.js";
export type { BasemapLayerListCompatOptions } from "./esri-compat/basemap-layer-list.js";
export { BookmarksCompat } from "./esri-compat/bookmarks.js";
export type { BookmarkCompatItem, BookmarksCompatOptions } from "./esri-compat/bookmarks.js";
export { ExpandCompat } from "./esri-compat/expand.js";
export type { ExpandCompatOptions } from "./esri-compat/expand.js";
export { GraphicsLayerCompat } from "./esri-compat/graphics-layer.js";
export type {
  GraphicsLayerCompatOptions,
  GraphicsLayerQueryResult,
} from "./esri-compat/graphics-layer.js";
export { GroupLayerCompat } from "./esri-compat/group-layer.js";
export type { GroupLayerCompatOptions } from "./esri-compat/group-layer.js";
export { parseFeatureLayerUrl, parseMapServiceUrl } from "./esri-compat/url.js";
export type { ParsedFeatureLayerUrl, ParsedMapServiceUrl } from "./esri-compat/url.js";
export {
  createArcGisTokenInterceptor,
  createEsriRequestInterceptors,
  EsriRequestInterceptorRegistry,
} from "./esri-compat/request.js";
export type {
  ArcGisTokenInterceptorOptions,
  EsriBeforeRequestParams,
  EsriRequestInterceptorHandle,
  EsriRequestInterceptorCompat,
  EsriRequestOptionsLike,
  EsriUrlPattern,
} from "./esri-compat/request.js";
export { MapCompat } from "./esri-compat/map.js";
export type { MapCompatOptions } from "./esri-compat/map.js";
export { MapImageLayerCompat } from "./esri-compat/map-image-layer.js";
export type {
  MapImageLayerIdentifyOptions,
  MapImageLayerCompatOptions,
  MapImageLayerExportOptions,
  MapImageLayerLegendOptions,
} from "./esri-compat/map-image-layer.js";
export { TileLayerCompat } from "./esri-compat/tile-layer.js";
export type { TileLayerCompatOptions } from "./esri-compat/tile-layer.js";
export { IdentifyCompat } from "./esri-compat/identify.js";
export type {
  IdentifyCompatLayerError,
  IdentifyCompatLayerResult,
  IdentifyCompatOptions,
  IdentifyCompatRequest,
  IdentifyCompatResult,
} from "./esri-compat/identify.js";
export { RouteLayerCompat } from "./esri-compat/route-layer.js";
export type {
  RouteLayerCompatOptions,
  RouteSolveResultCompat,
  RouteStopCompat,
} from "./esri-compat/route-layer.js";
export { RouteTaskCompat } from "./esri-compat/route-task.js";
export type {
  RouteTaskCompatOptions,
  RouteTaskDirectionsFeatureCompat,
  RouteTaskDirectionsSummaryCompat,
  RouteTaskResultGraphicCompat,
  RouteTaskRouteResultCompat,
  RouteTaskSolveParametersCompat,
  RouteTaskSolveResultCompat,
  RouteTaskStopFeatureCompat,
  RouteTaskStopsFeatureSetCompat,
} from "./esri-compat/route-task.js";
export { DirectionsCompat } from "./esri-compat/directions.js";
export type {
  DirectionsCompatOptions,
  DirectionsSolveSummaryCompat,
} from "./esri-compat/directions.js";
export { CoordinateConversionCompat } from "./esri-compat/coordinate-conversion.js";
export type {
  CoordinateConversionCompatOptions,
  CoordinateConversionResultCompat,
  CoordinateFormatCompat,
} from "./esri-compat/coordinate-conversion.js";
export { LayerListCompat } from "./esri-compat/layer-list.js";
export type { LayerListCompatOptions, LayerListItemCompat } from "./esri-compat/layer-list.js";
export { LegendCompat } from "./esri-compat/legend.js";
export type {
  LegendCompatOptions,
  LegendItemCompat,
  LegendLayerGroupCompat,
} from "./esri-compat/legend.js";
export { MapViewCompat } from "./esri-compat/map-view.js";
export type {
  MapViewCompatOptions,
  MapViewGoToTarget,
  MapViewHandle,
  MapViewHitTestEvent,
  MapViewHitTestResult,
  MapViewHitTestResultItem,
  MapViewMapPoint,
  MapViewPopupOpenOptions,
  MapViewScreenPoint,
  MapViewUiAddOptions,
  MapViewUiComponentRecord,
  MapViewUiPosition,
} from "./esri-compat/map-view.js";
export { MapViewLayerViewCompat, MapViewPopupCompat, MapViewUiCompat } from "./esri-compat/map-view.js";
export { PopupCompat } from "./esri-compat/popup.js";
export type {
  PopupCompatOptions,
  PopupHandleCompat,
  PopupOpenOptionsCompat,
} from "./esri-compat/popup.js";
export { PrintCompat } from "./esri-compat/print.js";
export type {
  PrintCompatOptions,
  PrintExecuteOptionsCompat,
  PrintResultCompat,
  PrintTemplateOptionsCompat,
} from "./esri-compat/print.js";
export { SceneViewCompat } from "./esri-compat/scene-view.js";
export type { SceneViewCompatOptions } from "./esri-compat/scene-view.js";
export { WebMapCompat } from "./esri-compat/web-map.js";
export type { WebMapCompatOptions } from "./esri-compat/web-map.js";
export { SearchCompat } from "./esri-compat/search.js";
export type {
  SearchCompatOptions,
  SearchRequestCompat,
  SearchResponseCompat,
  SearchResultCompat,
  SearchSourceCompat,
  SearchSuggestionCompat,
  SuggestResponseCompat,
} from "./esri-compat/search.js";
export { SwipeCompat } from "./esri-compat/swipe.js";
export type { SwipeCompatOptions } from "./esri-compat/swipe.js";
export { TrackCompat } from "./esri-compat/track.js";
export type { TrackCompatOptions, TrackPositionCompat } from "./esri-compat/track.js";
export { MeasurementCompat } from "./esri-compat/measurement.js";
export type {
  AreaUnitCompat,
  LinearUnitCompat,
  MeasurementCompatOptions,
  MeasurementResultCompat,
  MeasurementToolCompat,
} from "./esri-compat/measurement.js";
export { TimeSliderCompat } from "./esri-compat/time-slider.js";
export type {
  TimeExtentCompat,
  TimeSliderCompatOptions,
  TimeSliderIntervalUnitCompat,
  TimeSliderModeCompat,
  TimeSliderStopsCompat,
} from "./esri-compat/time-slider.js";
export { TableListCompat } from "./esri-compat/table-list.js";
export type { TableListCompatOptions } from "./esri-compat/table-list.js";
export { SketchCompat } from "./esri-compat/sketch.js";
export type {
  SketchCompatOptions,
  SketchCreateOptionsCompat,
  SketchCreateResultCompat,
  SketchCreationModeCompat,
  SketchToolCompat,
  SketchUpdateOptionsCompat,
} from "./esri-compat/sketch.js";
export { EditorCompat } from "./esri-compat/editor.js";
export type {
  EditorCompatOptions,
  EditorLayerInfoCompat,
  EditorWorkflowCompat,
} from "./esri-compat/editor.js";

export { scanArcGisUsage, summarizeArcGisScan } from "./migration/scanner.js";
export type { ArcGisImportHit, ArcGisScanReport } from "./migration/scanner.js";
export { runEsriCompatCodemod } from "./migration/codemod.js";
export type {
  CodemodConstructorKind,
  CodemodTarget,
  CodemodFileResult,
  CodemodKindMetrics,
  CodemodMetrics,
  CodemodMetricsByKind,
  EsriCompatCodemodOptions,
  EsriCompatCodemodResult,
  MigrationTodo,
} from "./migration/codemod.js";
export { SUPPORTED_ARCGIS_MODULES } from "./migration/codemod.js";
export { buildJsMigrationReport } from "./migration/report.js";
export type {
  ArcGisModuleSummary,
  ArcGisUsageStyle,
  JsMigrationReport,
  ManualRewriteMetric,
  ManualInterventionMetric,
  MigrationGateResult,
  MigrationReadiness,
  MigrationReasonSummary,
} from "./migration/report.js";
export { evaluateMigrationGates } from "./migration/gating.js";
export type { MigrationGateEvaluation, MigrationGateOptions } from "./migration/gating.js";
export { runLayerReconciliation, summarizeLayerReconciliation } from "./migration/reconcile.js";
export type { LayerReconciliationOptions, LayerReconciliationReport } from "./migration/reconcile.js";
export { getJsParityMatrix, JS_PARITY_MATRIX, summarizeJsParityMatrix } from "./migration/parity-matrix.js";
export type {
  JsParityCategory,
  JsParityMatrixEntry,
  JsParityMatrixKind,
  JsParityStatus,
  JsParitySummary,
} from "./migration/parity-matrix.js";
