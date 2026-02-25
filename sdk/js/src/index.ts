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

export { scanArcGisUsage, summarizeArcGisScan } from "./migration/scanner.js";
export type { ArcGisImportHit, ArcGisScanReport } from "./migration/scanner.js";
export { runEsriCompatCodemod } from "./migration/codemod.js";
export type {
  CodemodFileResult,
  CodemodMetrics,
  EsriCompatCodemodOptions,
  EsriCompatCodemodResult,
  MigrationTodo,
} from "./migration/codemod.js";
export { buildJsMigrationReport } from "./migration/report.js";
export type { JsMigrationReport, ManualRewriteMetric } from "./migration/report.js";
