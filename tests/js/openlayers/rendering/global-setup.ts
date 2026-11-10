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
  const baseUrl = process.env.HONUA_BASE_URL;
  if (!baseUrl) throw new Error('Set HONUA_BASE_URL to the server chosen for this rendering run.');

  if (!(await isHealthy(baseUrl))) {
    throw new Error(
      `Honua server is not reachable at ${baseUrl}. ` +
        'Set HONUA_BASE_URL to a running instance.',
    );
  }
}
