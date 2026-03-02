import type { HonuaClient } from "@honua/sdk-js";
import { encodeServiceId, mapWithConcurrency } from "../helpers.js";

export const uri = "honua://services";

const METADATA_CONCURRENCY = 8;

export async function read(client: HonuaClient) {
  const response = await client.listServices();
  const featureServices = (response.services ?? []).filter((s) => s.type === "FeatureServer");

  const catalog = await mapWithConcurrency(
    featureServices,
    METADATA_CONCURRENCY,
    async (s) => {
      try {
        const meta = await client.getFeatureServiceMetadata(s.name);
        return {
          serviceId: s.name,
          encodedServiceId: encodeServiceId(s.name),
          type: s.type,
          layers: meta.layers ?? [],
          tables: meta.tables ?? [],
          metadataError: null,
        };
      } catch (error) {
        const message = error instanceof Error ? error.message : String(error);
        return {
          serviceId: s.name,
          encodedServiceId: encodeServiceId(s.name),
          type: s.type,
          layers: [],
          tables: [],
          metadataError: message,
        };
      }
    },
  );

  return {
    contents: [
      {
        uri,
        mimeType: "application/json" as const,
        text: JSON.stringify(catalog, null, 2),
      },
    ],
  };
}
