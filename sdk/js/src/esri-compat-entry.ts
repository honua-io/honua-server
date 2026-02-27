export { FeatureLayerCompat } from "./esri-compat/feature-layer.js";
export type {
  FeatureLayerCreateQueryResult,
  FeatureLayerDeleteAttachmentsOptions,
  FeatureLayerHandleCompat,
  FeatureLayerLoadStatusCompat,
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
export { FeatureTableHighlightIdsCompat } from "./esri-compat/feature-table.js";
export type {
  FeatureTableCompatOptions,
  FeatureTableHighlightIdsChangeEventCompat,
  FeatureTableHandleCompat,
  FeatureTableHighlightIdsHandleCompat,
  FeatureTableQueryRelatedRecordsOptions,
  FeatureTableRowCompat,
  FeatureTableStateCompat,
} from "./esri-compat/feature-table.js";
export { FeatureSetCompat } from "./esri-compat/feature-set.js";
export type { FeatureSetCompatOptions } from "./esri-compat/feature-set.js";
export { ColorCompat } from "./esri-compat/color.js";
export type { ColorCompatInput } from "./esri-compat/color.js";
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
export { esriRequest } from "./esri-compat/esri-request.js";
export type {
  EsriRequestCompatOptions,
  EsriRequestCompatResponse,
  EsriRequestResponseTypeCompat,
} from "./esri-compat/esri-request.js";
export {
  esriConfig,
  getEsriConfigHonuaInterceptors,
  resetEsriConfig,
} from "./esri-compat/esri-config.js";
export type { EsriConfigCompat, EsriConfigRequestCompat } from "./esri-compat/esri-config.js";
export { identityManager } from "./esri-compat/identity-manager.js";
export type {
  IdentityCredentialCompat,
  IdentityTokenRegistrationCompat,
} from "./esri-compat/identity-manager.js";
export { OAuthInfoCompat } from "./esri-compat/oauth-info.js";
export type { OAuthInfoCompatOptions } from "./esri-compat/oauth-info.js";
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
  ControlHandleCompat,
  ControlLoadStatusCompat,
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
export type {
  BasemapGalleryCompatOptions,
  BasemapGalleryHandleCompat,
  BasemapGalleryLoadStatusCompat,
} from "./esri-compat/basemap-gallery.js";
export { BasemapCompat } from "./esri-compat/basemap.js";
export type {
  BasemapCompatOptions,
  BasemapHandleCompat,
  BasemapLoadStatusCompat,
} from "./esri-compat/basemap.js";
export { BasemapLayerListCompat } from "./esri-compat/basemap-layer-list.js";
export type {
  BasemapLayerListCompatOptions,
  BasemapLayerListHandleCompat,
  BasemapLayerListLoadStatusCompat,
} from "./esri-compat/basemap-layer-list.js";
export { BookmarksCompat } from "./esri-compat/bookmarks.js";
export type {
  BookmarkCompatItem,
  BookmarksCompatOptions,
  BookmarksHandleCompat,
  BookmarksLoadStatusCompat,
} from "./esri-compat/bookmarks.js";
export { ExpandCompat } from "./esri-compat/expand.js";
export type {
  ExpandCompatOptions,
  ExpandHandleCompat,
  ExpandLoadStatusCompat,
} from "./esri-compat/expand.js";
export { GraphicsLayerCompat } from "./esri-compat/graphics-layer.js";
export type {
  GraphicsLayerCompatOptions,
  GraphicsLayerHandleCompat,
  GraphicsLayerLoadStatusCompat,
  GraphicsLayerQueryResult,
} from "./esri-compat/graphics-layer.js";
export { GraphicCompat } from "./esri-compat/graphic.js";
export type { GraphicCompatOptions } from "./esri-compat/graphic.js";
export { PointCompat } from "./esri-compat/point.js";
export type { PointCompatOptions } from "./esri-compat/point.js";
export { PolylineCompat } from "./esri-compat/polyline.js";
export type { PolylineCompatOptions } from "./esri-compat/polyline.js";
export { PolygonCompat } from "./esri-compat/polygon.js";
export type { PolygonCompatOptions } from "./esri-compat/polygon.js";
export { ExtentCompat } from "./esri-compat/extent.js";
export type { ExtentCompatOptions } from "./esri-compat/extent.js";
export { SpatialReferenceCompat } from "./esri-compat/spatial-reference.js";
export type { SpatialReferenceCompatOptions } from "./esri-compat/spatial-reference.js";
export { SimpleLineSymbolCompat } from "./esri-compat/simple-line-symbol.js";
export type { SimpleLineSymbolCompatOptions } from "./esri-compat/simple-line-symbol.js";
export { SimpleFillSymbolCompat } from "./esri-compat/simple-fill-symbol.js";
export type { SimpleFillSymbolCompatOptions } from "./esri-compat/simple-fill-symbol.js";
export { SimpleMarkerSymbolCompat } from "./esri-compat/simple-marker-symbol.js";
export type { SimpleMarkerSymbolCompatOptions } from "./esri-compat/simple-marker-symbol.js";
export { PictureMarkerSymbolCompat } from "./esri-compat/picture-marker-symbol.js";
export type { PictureMarkerSymbolCompatOptions } from "./esri-compat/picture-marker-symbol.js";
export { TextSymbolCompat } from "./esri-compat/text-symbol.js";
export type { TextSymbolCompatOptions } from "./esri-compat/text-symbol.js";
export { LabelClassCompat } from "./esri-compat/label-class.js";
export type { LabelClassCompatOptions } from "./esri-compat/label-class.js";
export { ClassBreaksRendererCompat } from "./esri-compat/class-breaks-renderer.js";
export type {
  ClassBreakInfoCompat,
  ClassBreaksRendererCompatOptions,
} from "./esri-compat/class-breaks-renderer.js";
export { SimpleRendererCompat } from "./esri-compat/simple-renderer.js";
export type { SimpleRendererCompatOptions } from "./esri-compat/simple-renderer.js";
export { UniqueValueRendererCompat } from "./esri-compat/unique-value-renderer.js";
export type {
  UniqueValueInfoCompat,
  UniqueValueRendererCompatOptions,
} from "./esri-compat/unique-value-renderer.js";
export { GroupLayerCompat } from "./esri-compat/group-layer.js";
export type {
  GroupLayerCompatOptions,
  GroupLayerHandleCompat,
  GroupLayerLoadStatusCompat,
} from "./esri-compat/group-layer.js";
export { MapCompat } from "./esri-compat/map.js";
export type { MapCompatHandle, MapCompatOptions, MapLoadStatusCompat } from "./esri-compat/map.js";
export { MapImageLayerCompat } from "./esri-compat/map-image-layer.js";
export type {
  MapImageLayerHandleCompat,
  MapImageLayerIdentifyOptions,
  MapImageLayerCompatOptions,
  MapImageLayerExportOptions,
  MapImageLayerLoadStatusCompat,
  MapImageLayerLegendOptions,
} from "./esri-compat/map-image-layer.js";
export { TileLayerCompat } from "./esri-compat/tile-layer.js";
export type {
  TileLayerCompatOptions,
  TileLayerHandleCompat,
  TileLayerLoadStatusCompat,
} from "./esri-compat/tile-layer.js";
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
  RouteLayerHandleCompat,
  RouteLayerLoadStatusCompat,
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
  DirectionsHandleCompat,
  DirectionsLoadStatusCompat,
  DirectionsSolveSummaryCompat,
} from "./esri-compat/directions.js";
export { CoordinateConversionCompat } from "./esri-compat/coordinate-conversion.js";
export type {
  CoordinateConversionHandleCompat,
  CoordinateConversionLoadStatusCompat,
  CoordinateConversionCompatOptions,
  CoordinateConversionResultCompat,
  CoordinateFormatCompat,
} from "./esri-compat/coordinate-conversion.js";
export { LayerListCompat } from "./esri-compat/layer-list.js";
export type {
  LayerListActionCompat,
  LayerListCompatOptions,
  LayerListHandleCompat,
  LayerListItemCompat,
  LayerListLoadStatusCompat,
  LayerListListItemCreatedEventCompat,
  LayerListTriggerActionEventCompat,
  LayerListUpdatedEventCompat,
} from "./esri-compat/layer-list.js";
export { LegendCompat } from "./esri-compat/legend.js";
export type {
  LegendHandleCompat,
  LegendCompatOptions,
  LegendItemCompat,
  LegendLayerGroupCompat,
  LegendLoadStatusCompat,
} from "./esri-compat/legend.js";
export {
  MapViewCompat,
  MapViewLayerViewCompat,
  MapViewPopupCompat,
  MapViewUiCompat,
} from "./esri-compat/map-view.js";
export type {
  MapViewCompatOptions,
  MapViewGoToExtentLike,
  MapViewGoToInput,
  MapViewGoToOptions,
  MapViewGoToPointLike,
  MapViewGoToTarget,
  MapViewHandle,
  MapViewHitTestEvent,
  MapViewHitTestResult,
  MapViewHitTestResultItem,
  MapViewLayerViewHighlightHandle,
  MapViewLayerViewHighlightOptions,
  MapViewLayerViewHighlightRecord,
  MapViewLoadStatusCompat,
  MapViewMapPoint,
  MapViewPopupOpenOptions,
  MapViewScreenPoint,
  MapViewUiAddOptions,
  MapViewUiComponentRecord,
  MapViewUiPosition,
} from "./esri-compat/map-view.js";
export { PopupCompat } from "./esri-compat/popup.js";
export type {
  PopupCompatOptions,
  PopupHandleCompat,
  PopupLoadStatusCompat,
  PopupOpenOptionsCompat,
} from "./esri-compat/popup.js";
export { PopupTemplateCompat } from "./esri-compat/popup-template.js";
export type { PopupTemplateCompatOptions } from "./esri-compat/popup-template.js";
export { reactiveUtils, watch, when, whenOnce } from "./esri-compat/reactive-utils.js";
export type {
  ReactiveUtilsHandleCompat,
  ReactiveUtilsWatchOptionsCompat,
  ReactiveUtilsWhenOptionsCompat,
} from "./esri-compat/reactive-utils.js";
export { QueryCompat } from "./esri-compat/query.js";
export type { QueryCompatOptions } from "./esri-compat/query.js";
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
  SearchHandleCompat,
  SearchLoadStatusCompat,
  SearchRequestCompat,
  SearchResponseCompat,
  SearchResultCompat,
  SearchSourceCompat,
  SearchSuggestionCompat,
  SuggestResponseCompat,
} from "./esri-compat/search.js";
export { SwipeCompat } from "./esri-compat/swipe.js";
export type {
  SwipeCompatOptions,
  SwipeHandleCompat,
  SwipeLoadStatusCompat,
} from "./esri-compat/swipe.js";
export { TrackCompat } from "./esri-compat/track.js";
export type {
  TrackCompatOptions,
  TrackHandleCompat,
  TrackLoadStatusCompat,
  TrackPositionCompat,
} from "./esri-compat/track.js";
export { MeasurementCompat } from "./esri-compat/measurement.js";
export type {
  AreaUnitCompat,
  LinearUnitCompat,
  MeasurementHandleCompat,
  MeasurementLoadStatusCompat,
  MeasurementCompatOptions,
  MeasurementResultCompat,
  MeasurementToolCompat,
} from "./esri-compat/measurement.js";
export { AreaMeasurement2DCompat, DistanceMeasurement2DCompat } from "./esri-compat/measurement-2d.js";
export type {
  AreaMeasurement2DHandleCompat,
  AreaMeasurement2DLoadStatusCompat,
  AreaMeasurement2DCompatOptions,
  DistanceMeasurement2DHandleCompat,
  DistanceMeasurement2DLoadStatusCompat,
  DistanceMeasurement2DCompatOptions,
} from "./esri-compat/measurement-2d.js";
export { TimeSliderCompat } from "./esri-compat/time-slider.js";
export type {
  TimeSliderHandleCompat,
  TimeSliderLoadStatusCompat,
  TimeExtentCompat,
  TimeSliderCompatOptions,
  TimeSliderIntervalUnitCompat,
  TimeSliderModeCompat,
  TimeSliderStopsCompat,
} from "./esri-compat/time-slider.js";
export { TableListCompat } from "./esri-compat/table-list.js";
export type {
  TableListCompatOptions,
  TableListHandleCompat,
  TableListLoadStatusCompat,
} from "./esri-compat/table-list.js";
export { SketchCompat } from "./esri-compat/sketch.js";
export type {
  SketchCompatOptions,
  SketchCreateOptionsCompat,
  SketchCreateResultCompat,
  SketchCreationModeCompat,
  SketchHandleCompat,
  SketchLoadStatusCompat,
  SketchToolCompat,
  SketchUpdateOptionsCompat,
} from "./esri-compat/sketch.js";
export { EditorCompat } from "./esri-compat/editor.js";
export type {
  EditorHandleCompat,
  EditorLoadStatusCompat,
  EditorCompatOptions,
  EditorLayerInfoCompat,
  EditorWorkflowCompat,
} from "./esri-compat/editor.js";
