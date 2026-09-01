import { HonuaClient } from "@honua/sdk";
import fs from "node:fs";

const baseUrl = process.env.HONUA_SERVER_BASE_URL ?? "http://localhost:5000";
const serviceId = process.env.HONUA_SDK_SERVICE_ID ?? "test_service";
const client = new HonuaClient({ baseUrl });
const cases = [
  ["serve.geoservices-root", "HonuaClient.listServices", () => client.listServices()],
  ["serve.geoservices-featureserver", "HonuaClient.getFeatureServiceMetadata", () => client.getFeatureServiceMetadata(serviceId)],
  ["serve.geoservices-featureserver", "HonuaClient.queryFeatures", () => client.queryFeatures({ serviceId, layerId: 0, where: "1=1", resultRecordCount: 1 })],
  ["serve.ogc-api-features", "HonuaClient.getOgcFeaturesLanding", () => client.getOgcFeaturesLanding()],
  ["serve.ogc-api-features", "HonuaClient.listOgcCollections", () => client.listOgcCollections()],
  ["serve.stac", "HonuaClient.getStacLanding", () => client.getStacLanding()],
  ["serve.odata", "HonuaClient.odata.query", () => client.odata("Layers").query({ top: 1 })]
];
const observations = [];
for (const [capability, operation, invoke] of cases) {
  const startedAt = new Date().toISOString();
  try {
    await invoke();
    observations.push({ capability, operation, result: "pass", startedAt, completedAt: new Date().toISOString() });
  } catch (error) {
    observations.push({
      capability, operation, result: "fail", startedAt, completedAt: new Date().toISOString(),
      trace: String(error?.stack ?? error).slice(0, 8192)
    });
  }
}
fs.writeFileSync(process.argv[2], JSON.stringify({ observations }, null, 2) + "\n");
process.exitCode = observations.some(({ result }) => result !== "pass") ? 1 : 0;
