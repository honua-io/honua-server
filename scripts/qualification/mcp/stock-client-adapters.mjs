import { execFile } from 'node:child_process';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { CatalogContractFailure } from './catalog-channel-contract.mjs';
import { collectPages, openInstalledClient, ownedClientEndpoint, requirePublishedPackage } from './installed-client.mjs';
import { inspectorRateBudget } from './inspector-rate-budget.mjs';
import { acceptInspectorResult, classifyInspectorFailure } from './inspector-result.mjs';

const VIEW_META = 'honua.io/workflow-view';
const INSPECTOR_VERSION = '2.10.1';

export async function openStockSdk(baseUrl, apiKey) {
  const opened = await openInstalledClient({ baseUrl, apiKey, name: 'honua-stock-contract', timeoutMs: 180_000 });
  return {
    metadata: opened.metadata,
    discover: view => collectPages(async cursor => {
      const result = await opened.client.listTools({ cursor, _meta: { [VIEW_META]: view } }, { timeout: 10_000 });
      return { items: result.tools, nextCursor: result.nextCursor };
    }, 'incomplete-discovery'),
    call: (view, name, args) => opened.client.callTool({ name, arguments: args, _meta: { [VIEW_META]: view } }, undefined, { timeout: 10_000 }),
    close: () => opened.client.close(),
  };
}

function executeInspector(args, options) {
  return new Promise(resolve => {
    execFile(process.execPath, args, options, (error, stdout, stderr) => resolve({ error, stdout, stderr }));
  });
}

async function runInspector({ entry, config, directory, method, view, extra, deadline }) {
  const remaining = deadline - Date.now();
  if (remaining <= 0) throw new CatalogContractFailure('inspector-budget-exceeded');
  const args = [entry, '--cli', '--config', config, '--server', 'owned', '--method', method,
    '--format', 'json', '--quiet', '--metadata', `${VIEW_META}=${JSON.stringify(view)}`, ...extra];
  const env = Object.fromEntries(['PATH', 'SystemRoot', 'WINDIR', 'TMPDIR', 'TEMP', 'TMP']
    .filter(key => process.env[key] !== undefined).map(key => [key, process.env[key]]));
  Object.assign(env, { MCP_STORAGE_DIR: directory, MCP_INSPECTOR_SECRET_STORE: 'memory', MCP_AUTO_OPEN_ENABLED: 'false' });
  const { error, stdout, stderr } = await executeInspector(args, {
    env, timeout: Math.min(30_000, remaining), maxBuffer: 8 * 1024 * 1024,
  });
  try {
    return acceptInspectorResult(stdout, error?.code ?? 0);
  } catch {
    throw new CatalogContractFailure('inspector-invocation-failed',
      classifyInspectorFailure(stderr, error, { method, view, extra, stdout }));
  }
}

export async function openStockInspector(baseUrl, apiKey, { budget, rosters } = {}) {
  const endpoint = ownedClientEndpoint(baseUrl, apiKey);
  const installed = await requirePublishedPackage('@modelcontextprotocol/inspector', '@modelcontextprotocol/inspector/package.json', INSPECTOR_VERSION);
  const directory = await mkdtemp(join(tmpdir(), 'honua-mcp-stock-'));
  try {
    const config = join(directory, 'session.json');
    await writeFile(config, JSON.stringify({ mcpServers: { owned: {
      type: 'streamable-http', url: endpoint.href, protocolEra: 'legacy', requestTimeout: 20_000,
      headers: { 'X-API-Key': apiKey },
    } } }), { mode: 0o600 });
    const entry = join(installed.root, 'clients/launcher/build/index.js');
    const deadline = Date.now() + 15 * 60_000;
    const pacing = budget ? inspectorRateBudget({ ...budget, rosters }) : undefined;
    const invoke = async (method, view, extra = []) => {
      await pacing?.beforeInvocation(view, deadline);
      return runInspector({ entry, config, directory, method, view, extra, deadline });
    };
    return {
      metadata: { inspectorVersion: installed.version, inspectorIntegrity: installed.integrity, clientLockSha256: installed.lockSha256, rateBudget: pacing?.stats },
      discover: async view => (await invoke('tools/list', view)).tools,
      call: (view, name, args) => invoke('tools/call', view, ['--tool-name', name, '--tool-args-json', JSON.stringify(args)]),
      close: () => rm(directory, { recursive: true, force: true }),
    };
  } catch (error) {
    await rm(directory, { recursive: true, force: true });
    throw error;
  }
}
