/**
 * Playwright global setup: verifies Honua server is reachable before rendering tests.
 *
 * The server is expected to be started externally:
 *   - In CI, the setup-honua-server action handles this.
 *   - Locally, run "npm test" first (Vitest's globalSetup bootstraps the server).
 *   - Or set HONUA_BASE_URL to a running instance.
 */

const DEFAULT_PORT = '5555';

async function isHealthy(baseUrl: string): Promise<boolean> {
  const controller = new AbortController();
  const timeoutId = setTimeout(() => controller.abort(), 3000);
  try {
    const resp = await fetch(`${baseUrl}/healthz/live`, { signal: controller.signal });
    return resp.ok;
  } catch {
    return false;
  } finally {
    clearTimeout(timeoutId);
  }
}

export default async function globalSetup(): Promise<void> {
  const baseUrl =
    process.env.HONUA_BASE_URL ??
    `http://localhost:${process.env.HONUA_TEST_PORT ?? DEFAULT_PORT}`;
  process.env.HONUA_BASE_URL = baseUrl;

  if (!(await isHealthy(baseUrl))) {
    throw new Error(
      `Honua server is not reachable at ${baseUrl}. ` +
        'Start it first by running "npm test" (which bootstraps the server), ' +
        'or set HONUA_BASE_URL to a running instance.',
    );
  }
}
