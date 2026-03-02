import { z } from "zod";
import type { HonuaClient } from "@honua/sdk-js";
import { jsonText } from "../helpers.js";

export const schema = z.object({
  serviceId: z.string().describe("The feature service ID"),
  layerId: z.number().int().describe("The layer ID within the service"),
});

export type Input = z.infer<typeof schema>;

export async function execute(client: HonuaClient, input: Input) {
  const meta = await client.getLayerMetadata(input.serviceId, input.layerId);

  return jsonText({
    id: meta.id,
    name: meta.name,
    description: meta.description ?? null,
    geometryType: meta.geometryType ?? null,
    fields: (meta.fields ?? []).map((f) => ({ name: f.name, type: f.type, alias: f.alias ?? f.name })),
    extent: meta.extent ?? null,
    spatialReference: meta.spatialReference ?? null,
    relationships: meta.relationships ?? [],
  });
}
