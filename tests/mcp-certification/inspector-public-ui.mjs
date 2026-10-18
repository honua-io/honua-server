// Public installed launcher and rendered controls only. Never import Inspector
// internals, evaluate its client in the page, inject protocol history or modify
// its network requests. The independent observer owns all MCP wire assertions.
import { spawn } from 'node:child_process';
import { createServer } from 'node:net';
import { mkdtemp, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { until } from './postgres-cancellation-observer.mjs';

async function port() {
  const server = createServer();
  await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
  const result = server.address().port;
  await new Promise((resolve) => server.close(resolve));
  return result;
}

export async function openInspectorPublicUi({ inspectorEntry, chromium, endpoint, headers = {},
  toolLabel = 'honua_query_features', onControlFailure }) {
  const directory = await mkdtemp(join(tmpdir(), 'honua-inspector-ui-'));
  let child; let browser; let page; let closed = false; let exit;
  let stage = 'configuration';
  const close = async () => {
    if (closed) return;
    closed = true;
    const errors = [];
    try { await browser?.close(); } catch (error) { errors.push(error); }
    try {
      if (child && child.exitCode === null && child.signalCode === null) {
        child.kill('SIGTERM');
        let timer;
        try {
          await Promise.race([exit, new Promise((_, reject) => {
            timer = setTimeout(() => { child.kill('SIGKILL'); reject(new Error('Inspector cleanup exceeded its budget')); }, 3_000);
          })]);
        } finally { clearTimeout(timer); }
      }
    } catch (error) { errors.push(error); }
    try { await rm(directory, { recursive: true, force: true }); } catch (error) { errors.push(error); }
    if (errors.length) throw new AggregateError(errors, 'public Inspector cleanup failed');
  };
  try {
    const config = join(directory, 'session.json');
    await writeFile(config, JSON.stringify({ mcpServers: { certification: { type: 'streamable-http',
      url: endpoint, protocolEra: 'legacy', requestTimeout: 20_000, headers } } }), { mode: 0o600 });
    const clientPort = await port();
    const env = Object.fromEntries(['PATH', 'SystemRoot', 'WINDIR', 'TMPDIR', 'TEMP', 'TMP']
      .filter((key) => process.env[key] !== undefined).map((key) => [key, process.env[key]]));
    Object.assign(env, { CLIENT_PORT: String(clientPort), MCP_SANDBOX_PORT: '0', MCP_APP_ORIGIN_PORT: '0',
      MCP_STORAGE_DIR: join(directory, 'storage'), MCP_INSPECTOR_SECRET_STORE: 'memory', MCP_AUTO_OPEN_ENABLED: 'false' });
    stage = 'launcher';
    child = spawn(process.execPath, [inspectorEntry, '--web', '--config', config, '--server', 'certification'], {
      cwd: directory, env, stdio: ['ignore', 'pipe', 'pipe'],
    });
    exit = new Promise((resolve) => { child.once('error', resolve); child.once('close', resolve); });
    // Startup output can contain a local authentication token. Bound and discard
    // it; never emit it in receipts or failure messages.
    let outputBytes = 0;
    for (const stream of [child.stdout, child.stderr]) stream.on('data', (bytes) => {
      outputBytes += bytes.length;
      if (outputBytes > 1024 * 1024) child.kill('SIGTERM');
    });
    const origin = `http://127.0.0.1:${clientPort}`;
    await until(async () => {
      if (child.exitCode !== null || child.signalCode !== null) throw new Error('public Inspector launcher exited');
      try { return (await fetch(`${origin}/healthz`, { signal: AbortSignal.timeout(500), redirect: 'error' })).ok; }
      catch { return false; }
    }, 15_000);
    stage = 'browser';
    browser = await chromium.launch({ headless: true });
    page = await browser.newPage();
    page.setDefaultTimeout(8_000);
    await page.goto(origin, { waitUntil: 'domcontentloaded', timeout: 10_000 });
    stage = 'public connection switch';
    // --config supplies the single public server entry; --server is advisory
    // in the web launcher. Connect using the actual rendered switch.
    const connect = page.getByRole('switch', { name: /^Connect or disconnect/ });
    if (!(await connect.isChecked())) await connect.check();
    const tools = page.getByRole('tab', { name: /^Tools/ });
    if (await tools.count()) await tools.click();
    stage = 'public tool selection';
    await page.getByText(toolLabel, { exact: true }).first().click();
    stage = 'public argument editor';
    const editJson = page.getByLabel('Edit as JSON', { exact: true });
    if (!(await editJson.isChecked())) await editJson.check();
    const task = page.getByLabel('Run as task', { exact: true });
    if (await task.count()) { if (await task.isChecked()) await task.uncheck(); }
    const execute = async (arguments_) => {
      const editor = page.getByRole('textbox', { name: 'Arguments JSON', exact: true });
      await editor.click();
      await editor.press('ControlOrMeta+A');
      await page.keyboard.insertText(JSON.stringify(arguments_));
      await page.getByRole('button', { name: 'Execute Tool', exact: true }).click();
    };
    let savedArguments;
    return {
      async execute(arguments_) { savedArguments = arguments_; await execute(arguments_); },
      async cancel() { await page.getByRole('button', { name: 'Cancel', exact: true }).click(); },
      async recover() {
        await page.getByRole('button', { name: 'Execute Tool', exact: true }).waitFor({ state: 'visible' });
        await execute(savedArguments);
        await page.getByRole('button', { name: 'Cancel', exact: true }).waitFor({ state: 'hidden' });
      },
      close,
    };
  } catch (error) {
    // Used only by authored controls with dummy credentials. Candidate adapters
    // do not request page diagnostics, which could expose operator input.
    if (onControlFailure && page) await onControlFailure(stage, page, error).catch(() => {});
    await close();
    throw new Error(`public Inspector UI launch or interaction failed at ${stage}`);
  }
}
