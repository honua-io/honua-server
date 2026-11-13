import assert from "node:assert/strict";
import test from "node:test";
import { requestCancellation } from "../request-cancellation.mjs";
import { queryArguments } from "../inspector-browser-cancellation.mjs";
import { fixture } from "./cancellation-fixture.mjs";

const selectedFixture = { ...fixture, serviceLayerIndex: 7, storageLayerId: 41 };

test("Inspector query uses the service index without a storage or obsolete selector", () => {
  const arguments_ = queryArguments(selectedFixture);
  assert.equal(arguments_.serviceId, selectedFixture.serviceName);
  assert.equal(arguments_.serviceLayerIndex, 7);
  assert.equal(Object.hasOwn(arguments_, "layerId"), false);
  assert.equal(Object.hasOwn(arguments_, "storageLayerId"), false);
});

test("SDK cancellation driver passes the service index to callTool", async () => {
  const transport = { sessionId: "original-session", async send() {} };
  const otherTransport = { sessionId: "other-session" };
  const requests = [];
  const client = {
    callTool(request) {
      requests.push(request);
      return Promise.reject(new Error("stop after capturing the SDK request"));
    },
  };
  const observer = {
    async snapshot() { return "fixture-snapshot"; },
    async hold() { return 101; },
    async release() {},
  };
  await assert.rejects(requestCancellation({ client, transport,
    otherClient: { async close() {} }, otherTransport, observer,
    fixture: selectedFixture, verifyRecovery() {} }));

  assert.equal(requests.length, 1);
  assert.equal(requests[0].name, "honua_query_features");
  assert.equal(requests[0].arguments.serviceId, selectedFixture.serviceName);
  assert.equal(requests[0].arguments.serviceLayerIndex, 7);
  assert.equal(Object.hasOwn(requests[0].arguments, "layerId"), false);
  assert.equal(Object.hasOwn(requests[0].arguments, "storageLayerId"), false);
});
