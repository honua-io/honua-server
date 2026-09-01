import { HonuaClient } from "@honua/sdk";
import fs from "node:fs";

const baseUrl = process.env.HONUA_SERVER_BASE_URL ?? "http://localhost:5000";
const serviceId = process.env.HONUA_SDK_SERVICE_ID ?? "test_service";
const client = new HonuaClient({ baseUrl });
const requirePayload = (value, ...needles) => {
  const payload = JSON.stringify(value) ?? String(value);
  if (value == null || payload === "{}" || payload === "[]" || !needles.every((needle) => payload.includes(needle))) {
    throw new Error(`SDK response failed invariant (${needles.join(", ")}): ${payload.slice(0, 1024)}`);
  }
};
const cases = [
  ["serve.geoservices-root", "HonuaClient.listServices", async () => requirePayload(await client.listServices(), serviceId)],
  ["serve.geoservices-featureserver", "HonuaClient.getFeatureServiceMetadata", async () => requirePayload(await client.getFeatureServiceMetadata(serviceId), "Test Feature Service", "layers")],
  ["serve.geoservices-featureserver", "HonuaClient.queryFeatures", async () => requirePayload(await client.queryFeatures({ serviceId, layerId: 0, where: "1=1", resultRecordCount: 1 }), "alpha")],
  ["serve.ogc-api-features", "HonuaClient.getOgcFeaturesLanding", async () => requirePayload(await client.getOgcFeaturesLanding(), "links")],
  ["serve.ogc-api-features", "HonuaClient.listOgcCollections", async () => requirePayload(await client.listOgcCollections(), serviceId)],
  ["serve.stac", "HonuaClient.getStacLanding", async () => requirePayload(await client.getStacLanding(), "links")],
  ["serve.odata", "HonuaClient.odata.query", async () => requirePayload(await client.odata("Layers").query({ top: 1 }), "Test Layer")]
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
