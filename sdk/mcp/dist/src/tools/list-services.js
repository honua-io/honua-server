import { z } from "zod";
import { jsonText, mapWithConcurrency } from "../helpers.js";
export const schema = z.object({
    includeDetails: z.boolean().optional().default(false).describe("Fetch service metadata (description, layer count, spatial reference). Slower due to per-service requests."),
});
const METADATA_CONCURRENCY = 8;
export async function execute(client, input) {
    const response = await client.listServices();
    const services = (response.services ?? []).filter((s) => s.type === "FeatureServer");
    if (!input.includeDetails) {
        return jsonText(services.map((s) => ({ serviceId: s.name, type: s.type })));
    }
    const detailed = await mapWithConcurrency(services, METADATA_CONCURRENCY, async (s) => {
        try {
            const meta = await client.getFeatureServiceMetadata(s.name);
            return {
                serviceId: s.name,
                type: s.type,
                description: meta.serviceDescription ?? null,
                layerCount: (meta.layers?.length ?? 0) + (meta.tables?.length ?? 0),
                spatialReference: meta.spatialReference ?? null,
                metadataError: null,
            };
        }
        catch (error) {
            const message = error instanceof Error ? error.message : String(error);
            return {
                serviceId: s.name,
                type: s.type,
                description: null,
                layerCount: null,
                spatialReference: null,
                metadataError: message,
            };
        }
    });
    return jsonText(detailed);
}
