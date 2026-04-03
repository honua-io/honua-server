// Global setup for MapLibre browser compatibility tests.
// In CI the server is pre-started by the setup-honua-server action; this
// setup only verifies health before handing off to Playwright.
// Locally, start a server seeded with browser-compat.yaml and set
// HONUA_BASE_URL before running the suite.
// See docs/contributor/testing-maplibre-browser.md for local run instructions.

async function isHealthy(baseUrl: string): Promise<boolean> {
  const controller = new AbortController();
  const timeoutId = setTimeout(() => controller.abort(), 2000);
  try {
    const response = await fetch(`${baseUrl}/healthz/ready`, { signal: controller.signal });
    return response.ok;
  } catch {
    return false;
  } finally {
    clearTimeout(timeoutId);
  }
}

export default async function globalSetup() {
  const baseUrl = process.env.HONUA_BASE_URL ?? 'http://localhost:5000';
  if (await isHealthy(baseUrl)) {
    process.env.HONUA_BASE_URL = baseUrl;
    return;
  }

  throw new Error(
    `No healthy Honua server found at ${baseUrl}.\n` +
    'The MapLibre browser suite requires a server seeded with tests/seed/browser-compat.yaml ' +
    '(layers 2000-2002).\n' +
    'Set HONUA_BASE_URL to point at a running, seeded server.\n' +
    'See docs/contributor/testing-maplibre-browser.md for local setup instructions.',
  );
}
