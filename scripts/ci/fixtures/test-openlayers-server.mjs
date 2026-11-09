import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createServer } from 'node:net';
import { once } from 'node:events';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

test('OpenLayers readiness belongs to the per-run nonce', async () => {
  const reservation = createServer();
  reservation.listen(0, '127.0.0.1');
  await once(reservation, 'listening');
  const port = reservation.address().port;
  await new Promise(resolve => reservation.close(resolve));
  const child = spawn(process.execPath, [
    fileURLToPath(new URL('../../../tests/js/node_modules/tsx/dist/cli.mjs', import.meta.url)),
    fileURLToPath(new URL('../../../tests/js/openlayers/rendering/serve-test-page.ts', import.meta.url))], {
    env: { ...process.env, OL_TEST_PAGE_PORT: String(port), OL_TEST_PAGE_NONCE: 'owned-control' },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  try {
    await once(child.stdout, 'data');
    const base = `http://127.0.0.1:${port}/__honua_test_page/`;
    const own = await fetch(base + 'owned-control');
    assert.equal(own.status, 200);
    const foreign = await fetch(base + 'another-run');
    assert.equal(foreign.status, 404);
  } finally {
    const exit = once(child, 'exit');
    child.kill('SIGTERM');
    await exit;
  }
});
