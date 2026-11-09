import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { mkdtemp, mkdir, writeFile, rm, symlink } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { bootstrapHonuaServer } from '../../../tests/shared/js-bootstrap.ts';

const variables = ['CI', 'HONUA_BASE_URL', 'HONUA_TEST_PORT', 'HONUA_SERVICE_ID', 'HONUA_LAYER_ID'];
const ownedServer = `
import json,os
from http.server import BaseHTTPRequestHandler,HTTPServer
class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200)
        self.end_headers()
    def log_message(self,*args): pass
server=HTTPServer(('127.0.0.1',int(os.environ['HONUA_TEST_PORT'])),Handler)
print(json.dumps({'base_url':'http://127.0.0.1:'+str(server.server_port),'service_id':'owned-fixture','layer_id':42}),flush=True)
server.serve_forever()
`;

async function fixture(run) {
  const previous = Object.fromEntries(variables.map(key => [key, process.env[key]]));
  for (const key of variables) delete process.env[key];
  const projectRoot = await mkdtemp(join(tmpdir(), 'honua-bootstrap-ownership-'));
  const helper = join(projectRoot, 'tests/python/shared/js_test_server.py');
  await mkdir(join(projectRoot, 'tests/python/shared'), { recursive: true });
  await mkdir(join(projectRoot, 'scripts/ci'), { recursive: true });
  await symlink(fileURLToPath(new URL('../owned-server.py', import.meta.url)), join(projectRoot, 'scripts/ci/owned-server.py'));
  const foreign = createServer((_, response) => { response.writeHead(200); response.end(); });
  await new Promise(resolve => foreign.listen(0, '127.0.0.1', resolve));
  const port = foreign.address().port;
  let context;
  try {
    await run({ projectRoot, helper, port, retain: value => { context = value; } });
  } finally {
    await context?.teardown();
    foreign.closeAllConnections();
    await new Promise(resolve => foreign.close(resolve));
    await rm(projectRoot, { recursive: true, force: true });
    for (const key of variables) {
      if (previous[key] === undefined) delete process.env[key];
      else process.env[key] = previous[key];
    }
  }
}

function options(projectRoot, port) {
  return { projectRoot, defaultPort: String(port), label: 'ownership control', readinessTimeoutMs: 250 };
}

test('an implicit healthy default listener cannot replace the owned bootstrap', async () => {
  await fixture(async ({ projectRoot, helper, port, retain }) => {
    await writeFile(helper, ownedServer);
    retain(await bootstrapHonuaServer(options(projectRoot, port)));
    assert.notEqual(new URL(process.env.HONUA_BASE_URL).port, String(port));
    assert.equal(process.env.HONUA_SERVICE_ID, 'owned-fixture');
  });
});

test('an explicit external URL remains a deliberate probe', async () => {
  await fixture(async ({ projectRoot, port, retain }) => {
    process.env.HONUA_BASE_URL = `http://127.0.0.1:${port}`;
    retain(await bootstrapHonuaServer(options(projectRoot, port)));
    assert.equal(process.env.HONUA_BASE_URL, `http://127.0.0.1:${port}`);
  });
});

test('an exited bootstrap cannot qualify a foreign listener', async () => {
  await fixture(async ({ projectRoot, helper, port, retain }) => {
    await writeFile(helper, `import json\nprint(json.dumps({'base_url':'http://127.0.0.1:${port}','service_id':'wrong','layer_id':1}),flush=True)\n`);
    await assert.rejects(async () => {
      const context = await bootstrapHonuaServer(options(projectRoot, port));
      retain(context);
    }, /exited|ownership|healthy/);
  });
});

test('a live bootstrap without the listener cannot qualify foreign health', async () => {
  await fixture(async ({ projectRoot, helper, port, retain }) => {
    await writeFile(helper, `import json,time\nprint(json.dumps({'base_url':'http://127.0.0.1:${port}','service_id':'wrong','layer_id':1}),flush=True)\ntime.sleep(30)\n`);
    await assert.rejects(async () => {
      const context = await bootstrapHonuaServer(options(projectRoot, port));
      retain(context);
    }, /ownership|healthy/);
  });
});
