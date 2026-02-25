export { HonuaClient } from "./core/client.js";
export { HonuaHttpError } from "./core/errors.js";
export type {
  ApplyEditsRequest,
  HonuaClientOptions,
  QueryFeaturesRequest,
  QueryMethod,
} from "./core/types.js";

export { FeatureLayerCompat } from "./esri-compat/feature-layer.js";
export { parseFeatureLayerUrl } from "./esri-compat/url.js";
export type { ParsedFeatureLayerUrl } from "./esri-compat/url.js";
