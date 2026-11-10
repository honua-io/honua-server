import { spawn, spawnSync } from 'node:child_process';
import { access } from 'node:fs/promises';
import { resolve } from 'node:path';

/**
 * Shared bootstrap helpers for the JS test suites (Vitest and Playwright).
 *
 * Both `tests/js/` (Vitest) and `tests/js-browser/` (Playwright) need the same
 * routine: probe a Honua server, and if none is healthy, spawn the Python
 * `js_test_server.py` helper that brings up PostGIS + the .NET server, then
 * publish the resolved IDs into `process.env`. This module owns that logic so
 * the two runner-specific entry points stay tiny.
 */

/** Resolved bootstrap context, used by callers that need to clean up. */
export interface BootstrapContext {
  /** Returns a teardown function that stops the bootstrapped server, if any. */
  teardown: () => Promise<void>;
}

/** Options that vary between the Vitest and Playwright lanes. */
export interface BootstrapOptions {
  readinessTimeoutMs?: number;
  /** Default port to use when `HONUA_TEST_PORT` is not set. */
  defaultPort: string;
  /** Label included in error messages, e.g. "JS tests" or "browser tests". */
  label: string;
  /**
   * Absolute path to the repository root, used to locate the Python helper
   * and the test virtualenv. ESM callers must compute this from
   * `import.meta.url`; CJS callers can pass `resolve(__dirname, '..', '..')`.
   */
  projectRoot: string;
}

async function fileExists(path: string): Promise<boolean> {
  try {
    await access(path);
    return true;
  } catch {
    return false;
  }
}

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

async function waitForHealthy(baseUrl: string, ownsListener: () => boolean, timeoutMs: number): Promise<void> {
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    if (ownsListener() && await isHealthy(baseUrl) && ownsListener()) {
      return;
    }
    await new Promise(resolve => setTimeout(resolve, 500));
  }
  throw new Error(`Honua server did not become healthy within ${timeoutMs}ms (${baseUrl}).`);
}

async function stopBootstrap(child: ReturnType<typeof spawn>): Promise<void> {
  if (!child.pid || child.exitCode !== null || child.signalCode !== null) return;
  await new Promise<void>(resolve => {
    const timer = setTimeout(() => child.kill('SIGKILL'), 30000);
    child.once('exit', () => { clearTimeout(timer); resolve(); });
    child.kill('SIGTERM');
  });
}

async function readBootstrapLine(
  child: ReturnType<typeof spawn>,
  stderrBuffer: string[],
): Promise<string> {
  const stdout = child.stdout;
  if (!stdout) {
    throw new Error('Bootstrap process did not provide stdout.');
  }

  return new Promise((resolve, reject) => {
    const timeoutId = setTimeout(() => {
      reject(new Error('Timed out waiting for JS test server bootstrap.'));
    }, 120000);

    let buffer = '';

    const onStderr = (chunk: Buffer | string) => {
      stderrBuffer.push(chunk.toString());
    };

    const onError = (err: Error) => {
      cleanup();
      reject(err);
    };

    const onExit = (code: number | null) => {
      cleanup();
      reject(new Error(`Bootstrap process exited early with code ${code}.`));
    };

    const onStdout = (chunk: Buffer | string) => {
      buffer += chunk.toString();
      const newlineIndex = buffer.indexOf('\n');
      if (newlineIndex === -1) {
        return;
      }
      const line = buffer.slice(0, newlineIndex);
      cleanup();
      resolve(line);
    };

    const cleanup = () => {
      clearTimeout(timeoutId);
      stdout.off('data', onStdout);
      stdout.off('error', onError);
      child.off('error', onError);
      child.off('exit', onExit);
      if (child.stderr) {
        child.stderr.off('data', onStderr);
      }
    };

    if (child.stderr) {
      child.stderr.on('data', onStderr);
    }

    child.once('error', onError);
    child.once('exit', onExit);
    stdout.on('data', onStdout);
    stdout.once('error', onError);
  });
}

/**
 * Ensure a healthy Honua server is reachable, bootstrapping the local stack if
 * necessary, and publish the resolved service/layer IDs into `process.env`.
 *
 * Returns a `BootstrapContext` whose `teardown()` stops any spawned process.
 */
export async function bootstrapHonuaServer(
  options: BootstrapOptions,
): Promise<BootstrapContext> {
  const { defaultPort, label, projectRoot } = options;
  const pythonScript = resolve(projectRoot, 'tests', 'python', 'shared', 'js_test_server.py');
  const venvPython = resolve(projectRoot, '.venv-tests', 'bin', 'python');

  process.env.HONUA_SERVICE_ID ??= 'test_service_gw0';
  process.env.HONUA_LAYER_ID ??= '1000';
  const baseUrl = process.env.HONUA_BASE_URL;
  if (baseUrl) {
    if (!(await isHealthy(baseUrl))) throw new Error(`Explicit Honua server is not healthy for ${label}.`);
    return { teardown: async () => {} };
  }

  const python = (await fileExists(venvPython)) ? venvPython : 'python3';
  const ownershipScript = resolve(projectRoot, 'scripts', 'ci', 'owned-server.py');
  const selected = spawnSync(python, [ownershipScript, 'port', '--variable', 'HONUA_TEST_PORT',
    '--ci-default', defaultPort], { encoding: 'utf8', timeout: 5000 });
  if (selected.status !== 0) throw new Error('Cannot select the bootstrap listener port.');
  process.env.HONUA_TEST_PORT = selected.stdout.trim();
  const stderrBuffer: string[] = [];

  const child = spawn(python, [pythonScript], {
    cwd: projectRoot,
    env: {
      ...process.env,
      HONUA_TEST_PORT: process.env.HONUA_TEST_PORT ?? defaultPort,
    },
    stdio: ['ignore', 'pipe', 'pipe'],
  });

  try {
    const line = await readBootstrapLine(child, stderrBuffer);
    const info: { base_url: string; service_id: string; layer_id: number } = JSON.parse(line);
    const ownsListener = () => {
      if (child.exitCode !== null || child.signalCode !== null) throw new Error('Bootstrap process exited.');
      const proof = spawnSync(python, [ownershipScript, 'check', '--pid', String(child.pid),
        '--url', info.base_url], { encoding: 'utf8', timeout: 5000 });
      if (proof.status !== 0 && proof.status !== 1) throw new Error('Cannot verify bootstrap listener ownership.');
      return proof.status === 0;
    };
    await waitForHealthy(info.base_url, ownsListener, options.readinessTimeoutMs ?? 60000);
    process.env.HONUA_BASE_URL = info.base_url;
    process.env.HONUA_SERVICE_ID = info.service_id;
    process.env.HONUA_LAYER_ID = String(info.layer_id);
    return { teardown: () => stopBootstrap(child) };
  } catch (error) {
    const stderr = stderrBuffer.join('');
    await stopBootstrap(child);
    throw new Error(`Failed to bootstrap Honua server for ${label}: ${error}${stderr ? `\n${stderr}` : ''}`);
  }
}
