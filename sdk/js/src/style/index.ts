export {
  isHonuaSource,
  isFeatureServiceSource,
  isMapServiceSource,
  isOgcFeaturesSource,
  parseOgcFeaturesUrl,
  validateHonuaStyle,
} from "./specification.js";
export type {
  HonuaSourceBase,
  HonuaFeatureServiceSourceSpecification,
  HonuaMapServiceSourceSpecification,
  HonuaOgcFeaturesSourceSpecification,
  HonuaSourceSpecification,
  HonuaLayerSpecification,
  HonuaStyleSpecification,
  ParsedOgcFeaturesUrl,
  StyleValidationError,
} from "./specification.js";
export { createSources } from "./factory.js";
export type { ResolvedSource } from "./factory.js";
// Feature Service and Map Service URL parsers are exported from
// esri-compat/url.ts via the main index.ts barrel — no re-export needed here.
