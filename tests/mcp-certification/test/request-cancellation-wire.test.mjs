// Installed-SDK wire controls against a deliberately scripted peer. The provider
// observer here is synthetic; these tests never constitute Honua qualification.
import assert from "node:assert/strict";
import { createServer } from "node:http";
import test from "node:test";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StreamableHTTPClientTransport } from "@modelcontextprotocol/sdk/client/streamableHttp.js";
import { requestCancellation } from "../request-cancellation.mjs";
import { until } from "../postgres-cancellation-observer.mjs";
import { fixture, queryResult } from "./cancellation-fixture.mjs";

async function execute(mode) {
  let session = 0;
  let pending;
  let waiting = false;
  let queryCalls = 0;
  const calls = [];
  const json = (response, body, headers = {}) => response.writeHead(200, { "Content-Type": "application/json", ...headers }).end(JSON.stringify(body));
  const listener = createServer(async (request, response) => {
    if (request.method === "DELETE") { response.writeHead(200).end(); return; }
    if (request.method !== "POST") { response.writeHead(405).end(); return; }
    const chunks = [];
    for await (const chunk of request) chunks.push(chunk);
    const body = JSON.parse(Buffer.concat(chunks).toString());
    const sid = request.headers["mcp-session-id"];
    calls.push({ ...body, session: sid });
    if (body.method === "initialize") {
      json(response, { jsonrpc: "2.0", id: body.id, result: { protocolVersion: "2025-06-18", capabilities: { tools: {} },
        serverInfo: { name: "scripted-cancellation-control", version: "1" } } }, { "Mcp-Session-Id": `control-session-${++session}` });
      return;
    }
    if (body.method === "tools/call") {
      if (++queryCalls === 1) { pending = { response, id: body.id, session: sid }; waiting = true; return; }
      json(response, { jsonrpc: "2.0", id: body.id, result: { ...queryResult(),
        ...(mode === "recovery-error" ? { isError: true } : {}) } }); return;
    }
    if (body.method === "notifications/cancelled" && pending
      && (body.params.requestId === pending.id || (mode === "wrong-id-type" && String(body.params.requestId) === String(pending.id)))
      && (sid === pending.session || mode === "wrong-session")) {
      const original = pending; pending = undefined;
      waiting = mode === "acknowledgement-only";
      json(original.response, { jsonrpc: "2.0", id: original.id, error: { code: -32800, message: "Request cancelled.",
        data: { code: "cancelled" } }, ...(mode === "contradictory-result" ? { result: queryResult() } : {}) });
    }
    response.writeHead(202).end();
  });
  const client = new Client({ name: "installed-sdk-wire-control", version: "1" });
  const otherClient = new Client({ name: "installed-sdk-other-session", version: "1" });
  const observer = { tableSchema: "public", released: false, async snapshot() { return `sha256:${"2".repeat(64)}`; },
    async hold() { return 101; }, async blocked() { await until(() => pending, 2_000); return 102; },
    async state() { return { waiting, relationLocks: waiting ? 1 : 0, blockerHeld: !this.released }; },
    async release() { this.released = true; if (mode === "cleanup-failure") throw new Error("synthetic cleanup failure"); } };
  try {
    await new Promise((resolve) => listener.listen(0, "127.0.0.1", resolve));
    const url = new URL(`http://127.0.0.1:${listener.address().port}/mcp`);
    const transport = new StreamableHTTPClientTransport(url);
    const otherTransport = new StreamableHTTPClientTransport(url);
    await client.connect(transport); await otherClient.connect(otherTransport);
    let result;
    try { result = { proof: await requestCancellation({ client, transport, otherClient, otherTransport, observer, fixture, verifyRecovery: (result) => assert.deepEqual(result, queryResult()) }) }; }
    catch (error) { result = { error }; }
    assert.equal(observer.released, true);
    return { ...result, calls, queryCalls };
  } finally {
    if (pending) pending.response.destroy();
    await Promise.allSettled([client.close(), otherClient.close()]);
    listener.closeAllConnections();
    await new Promise((resolve) => listener.close(resolve));
  }
}

test("installed SDK keeps original HTTP open and receives typed cancellation through public notification", async () => {
  const result = await execute("positive");
  assert.equal(result.error, undefined, result.error?.message);
  assert.equal(result.proof.schema, "honua.mcp-request-cancellation-proof/v1");
  assert.equal(result.proof.clientAbortUsed, false);
  assert.equal(result.proof.provider.ceasedBeforeBlockerRelease, true);
  assert.equal(result.proof.provider.relationLocksReleased, true);
  assert.equal(result.proof.provider.beforeRowsSha256, result.proof.provider.afterRowsSha256);
  assert.deepEqual(result.proof.cleanup, { blockerReleased: true, otherClientClosed: true, originalRequestDrained: true });
  assert.equal(result.queryCalls, 2);
  const request = result.calls.find((call) => call.method === "tools/call");
  const cancellations = result.calls.filter((call) => call.method === "notifications/cancelled");
  assert.equal(cancellations.length, 4);
  assert.notEqual(cancellations[0].session, request.session);
  assert.equal(cancellations[0].params.requestId, request.id);
  assert.equal(cancellations[1].params.requestId, String(request.id));
  assert.equal(cancellations[2].params.requestId, request.id);
  assert.equal(cancellations[2].session, request.session);
});

for (const [mode, stage] of [["wrong-session", "wrong-session"], ["wrong-id-type", "wrong-id-type"],
  ["acknowledgement-only", "provider-cessation"], ["contradictory-result", "rightful-cancellation"],
  ["cleanup-failure", "recovery"], ["recovery-error", "recovery"]]) {
  test(`installed SDK proof rejects ${mode} instead of treating a notification as cessation`, async () => {
    const result = await execute(mode);
    assert.equal(result.proof, undefined);
    assert.match(result.error?.message ?? "", new RegExp(stage));
  });
}
