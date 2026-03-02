import { z } from "zod";
import { jsonText, mapSpatialRel } from "../helpers.js";
export const schema = z.object({
    serviceId: z.string().describe("The feature service ID"),
    layerId: z.number().int().describe("The layer ID within the service"),
    where: z.string().optional().describe('SQL WHERE clause, e.g. "status = \'active\'"'),
    geometry: z.record(z.unknown()).optional().describe("Esri JSON geometry for spatial filter"),
    spatialRel: z
        .enum(["intersects", "contains", "within"])
        .optional()
        .describe("Spatial relationship (default: intersects)"),
});
export async function execute(client, input) {
    const response = (await client.queryFeatures({
        serviceId: input.serviceId,
        layerId: input.layerId,
        where: input.where,
        geometry: input.geometry,
        spatialRel: mapSpatialRel(input.spatialRel),
        returnGeometry: false,
        outFields: "OBJECTID",
        extraParams: { returnCountOnly: true },
    }));
    const hasCount = Object.prototype.hasOwnProperty.call(response, "count");
    const count = response.count;
    if (!hasCount || typeof count !== "number" || !Number.isFinite(count)) {
        throw new Error("Count query did not return a numeric count.");
    }
    return jsonText({ count });
}
