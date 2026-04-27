async function isHealthy(baseUrl: string): Promise<boolean> {
  const controller = new AbortController();
  const timeoutId = setTimeout(() => controller.abort(), 2000);
  try {
    const response = await fetch(`${baseUrl}/healthz/live`, { signal: controller.signal });
    return response.ok;
  } catch {
    return false;
  } finally {
    clearTimeout(timeoutId);
  }
}

export default async function globalSetup(): Promise<void> {
  const baseUrl = process.env.HONUA_BASE_URL ?? 'http://localhost:5000';
  if (await isHealthy(baseUrl)) {
    process.env.HONUA_BASE_URL = baseUrl;
    return;
  }
  throw new Error(
    `Cesium browser tests require a running Honua Server seeded with tests/seed/browser-compat.yaml (${baseUrl}).`,
  );
}
