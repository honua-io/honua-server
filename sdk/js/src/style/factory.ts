/**
 * Style-to-source factory: creates Honua SDK surface instances from a
 * HonuaStyleSpecification's source definitions.
 *
 * @module
 */

import type { HonuaClient } from "../core/client.js";
import {
  HonuaFeatureLayer,
  HonuaMapService,
  HonuaOgcFeatures,
  type HonuaOgcFeatureCollection,
} from "../core/surfaces.js";
import { parseFeatureLayerUrl, parseMapServiceUrl } from "../esri-compat/url.js";
import type { HonuaStyleSpecification } from "./specification.js";
import {
  isFeatureServiceSource,
  isMapServiceSource,
  isOgcFeaturesSource,
  parseOgcFeaturesUrl,
} from "./specification.js";

/** A resolved Honua source: either a surface instance or `null` for non-Honua (MapLibre-native) sources. */
export type ResolvedSource =
  | HonuaFeatureLayer
  | HonuaMapService
  | HonuaOgcFeatureCollection
  | null;

/**
 * Create Honua SDK surface instances for each source in a style.
 *
 * Non-Honua sources (e.g. `vector`, `raster`, `geojson`) are mapped to `null` —
 * these should be handled directly by MapLibre's built-in source types.
 *
 * @example
 * ```ts
 * const sources = createSources(client, style);
 * const parcels = sources.get("parcels"); // HonuaFeatureLayer
 * if (parcels instanceof HonuaFeatureLayer) {
 *   const features = await parcels.queryFeatures({ where: "status = 'active'" });
 * }
 * ```
 */
export function createSources(
  client: HonuaClient,
  style: HonuaStyleSpecification,
): Map<string, ResolvedSource> {
  const result = new Map<string, ResolvedSource>();

  for (const [name, spec] of Object.entries(style.sources)) {
    if (isFeatureServiceSource(spec)) {
      const parsed = parseFeatureLayerUrl(spec.url);
      result.set(
        name,
        new HonuaFeatureLayer({
          client,
          serviceId: parsed.serviceId,
          layerId: parsed.layerId,
        }),
      );
    } else if (isMapServiceSource(spec)) {
      const parsed = parseMapServiceUrl(spec.url);
      result.set(
        name,
        new HonuaMapService({
          client,
          serviceId: parsed.serviceId,
        }),
      );
    } else if (isOgcFeaturesSource(spec)) {
      const parsed = parseOgcFeaturesUrl(spec.url);
      const ogcRoot = new HonuaOgcFeatures({ client });
      const collectionId = spec.collectionId ?? parsed.collectionId ?? "";
      result.set(name, ogcRoot.collection(collectionId));
    } else {
      result.set(name, null);
    }
  }

  return result;
}
