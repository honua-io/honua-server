import { z } from "zod";
import type { HonuaClient } from "@honua/sdk-js";
import { jsonText, mapSpatialRel } from "../helpers.js";

export const schema = z.object({
  serviceId: z.string().describe("The feature service ID"),
  layerId: z.number().int().describe("The layer ID within the service"),
  where: z.string().optional().describe("SQL WHERE clause"),
  geometry: z.record(z.unknown()).optional().describe("Esri JSON geometry for spatial filter"),
  spatialRel: z
    .enum(["intersects", "contains", "within"])
    .optional()
    .describe("Spatial relationship (default: intersects)"),
});

export type Input = z.infer<typeof schema>;

export async function execute(client: HonuaClient, input: Input) {
  const response = (await client.queryFeatures({
    serviceId: input.serviceId,
    layerId: input.layerId,
    where: input.where,
    geometry: input.geometry,
    spatialRel: mapSpatialRel(input.spatialRel),
    returnGeometry: false,
    extraParams: { returnExtentOnly: true },
  })) as Record<string, unknown>;

  const hasCount = Object.prototype.hasOwnProperty.call(response, "count");
  const count = hasCount && typeof response.count === "number" && Number.isFinite(response.count)
    ? response.count
    : undefined;

  const hasExtent = Object.prototype.hasOwnProperty.call(response, "extent");
  if (!hasExtent) {
    // Some backends can return count-only payloads for empty extent queries.
    if (count !== undefined) {
      return jsonText({ extent: null, count });
    }
    throw new Error("Extent query did not return an extent payload.");
  }

  let extent: Record<string, unknown> | null;
  if (response.extent === null) {
    extent = null;
  } else if (typeof response.extent === "object") {
    extent = response.extent as Record<string, unknown>;
  } else {
    throw new Error("Extent query returned an invalid extent payload.");
  }

  return jsonText({ extent, count });
}
