export { HonuaClient } from "./core/client.js";
export { HonuaHttpError } from "./core/errors.js";
export {
  createHonuaService,
  HonuaFeatureLayer,
  HonuaMapService,
  HonuaService,
} from "./core/surfaces.js";
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
export type {
  HonuaFeatureLayerApplyEditsRequest,
  HonuaFeatureLayerOptions,
  HonuaFeatureLayerQueryAllRequest,
  HonuaFeatureLayerQueryCountRequest,
  HonuaFeatureLayerQueryObjectIdsRequest,
  HonuaFeatureLayerQueryRelatedRecordsRequest,
  HonuaFeatureLayerQueryRequest,
  HonuaMapServiceExportMapRequest,
  HonuaMapServiceFindRequest,
  HonuaMapServiceIdentifyRequest,
  HonuaMapServiceLegendRequest,
  HonuaMapServiceOptions,
  HonuaServiceOptions,
} from "./core/surfaces.js";
