import { spawn } from 'node:child_process';
import { access } from 'node:fs/promises';
import { resolve } from 'node:path';

const projectRoot = resolve(__dirname, '..', '..');
const pythonScript = resolve(projectRoot, 'tests', 'python', 'shared', 'js_test_server.py');
const venvPython = resolve(projectRoot, '.venv-tests', 'bin', 'python');
const defaultPort = process.env.HONUA_TEST_PORT ?? '5555';

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

async function waitForHealthy(baseUrl: string, timeoutMs = 60000): Promise<void> {
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    if (await isHealthy(baseUrl)) {
      return;
    }
    await new Promise(resolve => setTimeout(resolve, 500));
  }
  throw new Error(`Honua server did not become healthy within ${timeoutMs}ms (${baseUrl}).`);
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

export default async function () {
  process.env.HONUA_SERVICE_ID ??= 'test_service_gw0';
  process.env.HONUA_LAYER_ID ??= '1000';
  process.env.HONUA_TEST_PORT ??= defaultPort;

  const baseUrl = process.env.HONUA_BASE_URL ?? `http://localhost:${defaultPort}`;
  if (await isHealthy(baseUrl)) {
    process.env.HONUA_BASE_URL = baseUrl;
    return;
  }

  const python = (await fileExists(venvPython)) ? venvPython : 'python3';
  const stderrBuffer: string[] = [];

  const child = spawn(python, [pythonScript], {
    cwd: projectRoot,
    env: {
      ...process.env,
      HONUA_TEST_PORT: process.env.HONUA_TEST_PORT ?? defaultPort,
    },
    stdio: ['ignore', 'pipe', 'pipe'],
  });

  let info: { base_url: string; service_id: string; layer_id: number };
  try {
    const line = await readBootstrapLine(child, stderrBuffer);
    info = JSON.parse(line);
  } catch (error) {
    const stderr = stderrBuffer.join('');
    child.kill('SIGTERM');
    throw new Error(`Failed to bootstrap Honua server for JS tests: ${error}${stderr ? `\n${stderr}` : ''}`);
  }

  process.env.HONUA_BASE_URL = info.base_url;
  process.env.HONUA_SERVICE_ID = info.service_id;
  process.env.HONUA_LAYER_ID = String(info.layer_id);

  await waitForHealthy(info.base_url);

  return async () => {
    child.kill('SIGTERM');
    await new Promise(resolve => {
      child.once('exit', () => resolve(null));
      setTimeout(resolve, 5000);
    });
  };
}
