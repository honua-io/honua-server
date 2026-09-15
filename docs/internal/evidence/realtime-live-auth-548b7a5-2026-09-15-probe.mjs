#!/usr/bin/env node
// Boundary probe for server#4776 / #4777 / #4778 on a candidate booted by
// realtime-live-auth-548b7a5-2026-09-15-replay.sh. Layer 10 belongs to tenant-a,
// layer 11 to tenant-b; both refuse anonymous callers and admit "reader".
// Writes a transcript of every cell (status, challenge, body shape, close frame,
// timing against the advertised expiry or revocation request). Credentials
// never reach the transcript. Exits non-zero when any cell fails.
import { createHmac, randomBytes } from "node:crypto";
import fs from "node:fs";
import { createRequire } from "node:module";
import { setTimeout as delay } from "node:timers/promises";

const require = createRequire(`${process.env.NODE_PATH}/`);
const WebSocket = require("ws");

const base = process.env.HONUA_REALTIME_CANDIDATE_BASE_URL;
const referer = process.env.HONUA_REALTIME_ISSUER_REFERER;
const issuer = {
  iss: process.env.HONUA_REALTIME_ISSUER,
  aud: process.env.HONUA_REALTIME_ISSUER_AUDIENCE,
  key: process.env.HONUA_REALTIME_ISSUER_SIGNING_KEY,
};
const output = process.argv[2];
const cells = [];
const layers = { "tenant-a": 10, "tenant-b": 11 };

function relayJwt(subject, tenant, roles) {
  const now = Math.floor(Date.now() / 1000);
  const claims = { iss: issuer.iss, aud: issuer.aud, sub: subject, name: subject, roles, iat: now, nbf: now, exp: now + 900, jti: randomBytes(12).toString("hex") };
  if (tenant) claims.tenant_id = tenant;
  const encode = (v) => Buffer.from(JSON.stringify(v)).toString("base64url");
  const unsigned = `${encode({ alg: "HS256", typ: "JWT" })}.${encode(claims)}`;
  return `${unsigned}.${createHmac("sha256", issuer.key).update(unsigned).digest("base64url")}`;
}

async function issue(subject, tenant) {
  const body = new URLSearchParams({ username: subject, password: relayJwt(subject, tenant, ["reader"]), client: "referer", referer, expiration: "1", f: "json" });
  const response = await fetch(`${base}/sharing/rest/generateToken`, { method: "POST", headers: { "Content-Type": "application/x-www-form-urlencoded", Referer: referer }, body });
  const json = await response.json();
  if (response.status !== 200 || typeof json.token !== "string") throw new Error(`generateToken ${response.status}`);
  return { token: json.token, expires: json.expires };
}

async function revoke(credential) {
  const requestedAt = Date.now();
  const response = await fetch(`${base}/sharing/rest/oauth2/revoke`, { method: "POST", headers: { "Content-Type": "application/x-www-form-urlencoded" }, body: new URLSearchParams({ token: credential.token }) });
  if (response.status !== 200) throw new Error(`revoke ${response.status}`);
  return requestedAt;
}

async function odata(layer, { bearer, queryToken } = {}) {
  const headers = { Accept: "application/json", Referer: referer, Prefer: "odata.track-changes" };
  if (bearer !== undefined) headers.Authorization = `Bearer ${bearer}`;
  const query = queryToken !== undefined ? `?token=${encodeURIComponent(queryToken)}` : "";
  const sentAt = Date.now();
  const response = await fetch(`${base}/odata/Features(${layer})${query}`, { headers });
  const text = await response.text();
  let json = null;
  try { json = JSON.parse(text); } catch { /* not json */ }
  return {
    status: response.status,
    sentAt,
    at: Date.now(),
    wwwAuthenticate: response.headers.get("www-authenticate"),
    contentType: response.headers.get("content-type"),
    payloadRows: Array.isArray(json?.value) ? json.value.length : null,
    error: json?.error?.code ?? json?.title ?? null,
    bodyBytes: text.length,
  };
}

function record(issueRef, name, expectation, observed, passed) {
  cells.push({ issue: issueRef, cell: name, expectation, observed, result: passed ? "passed" : "failed" });
  console.log(`${passed ? "PASS" : "FAIL"} ${issueRef} ${name}: ${JSON.stringify(observed)}`);
}

const challenged = (r) => r.status === 401 && /\bBearer\b/u.test(r.wwwAuthenticate ?? "") && r.payloadRows === null;

async function odataChallengeCell(name, request) {
  const r = await request();
  record("#4778", name, "401 + WWW-Authenticate Bearer, no rows", r, challenged(r));
}

function openFeatureSocket(credential, layer) {
  return new Promise((resolve, reject) => {
    const url = `${base.replace(/^http/u, "ws")}/api/v1/streaming/features?serviceId=test_service&layers=${layer}&token=${encodeURIComponent(credential.token)}`;
    const socket = new WebSocket(url, { headers: { Referer: referer } });
    const state = { socket, subscribed: false, close: null, error: null };
    state.closed = new Promise((done) => {
      socket.on("close", (code, reason) => { state.close = { code, reason: reason.toString("utf8"), at: Date.now() }; done(); });
    });
    socket.on("error", (error) => { state.error = String(error?.message ?? error); });
    socket.on("unexpected-response", (_req, res) => reject(new Error(`websocket upgrade ${res.statusCode}`)));
    socket.on("message", (data) => {
      try { if (JSON.parse(data.toString("utf8")).status === "subscribed" && !state.subscribed) { state.subscribed = true; resolve(state); } } catch { /* ignore */ }
    });
    setTimeout(() => reject(new Error("websocket did not subscribe within 15 s")), 15000);
  });
}

async function webSocketBoundaryCell(name, state, boundaryAt) {
  await Promise.race([state.closed, delay(Math.max(0, boundaryAt - Date.now()) + 15000)]);
  const c = state.close;
  const observed = c ? { code: c.code, reason: c.reason, msAfterBoundary: c.at - boundaryAt, error: state.error } : { close: null, error: state.error };
  record("#4776", name, "close 1008 authorization-ended within 5 s of the boundary", observed, !!c && c.code === 1008 && c.reason === "authorization-ended" && c.at - boundaryAt >= 0 && c.at - boundaryAt <= 5000);
}

// --- OData challenge cells (#4778) --------------------------------------------
const readerA = await issue("replay-reader-a", "tenant-a");
const readerB = await issue("replay-reader-b", "tenant-b");

const control = await odata(10, { bearer: readerA.token });
record("#4778", "odata/control/tenant-a-reader-own-layer", "200 with rows", control, control.status === 200 && control.payloadRows !== null);
// A caller that attempted no credential gets the shared challenge (#4778 asks for a
// WWW-Authenticate challenge); Bearer is advertised once a portal token was attempted.
for (const layer of [10, 11]) {
  const r = await odata(layer);
  record("#4778", `odata/anonymous/layer-${layer}`, "401 + a WWW-Authenticate challenge, no rows", r, r.status === 401 && !!r.wwwAuthenticate && r.payloadRows === null);
}
await odataChallengeCell("odata/invalid-bearer/layer-10", () => odata(10, { bearer: "not-a-portal-token" }));
// `token` is not an OData credential transport: refused as a query option, never served.
const queryToken = await odata(10, { queryToken: "not-a-portal-token" });
record("#4778", "odata/query-token-not-a-transport/layer-10", "refused (4xx), no rows", queryToken, queryToken.status >= 400 && queryToken.status < 500 && queryToken.payloadRows === null);
const crossBA = await odata(10, { bearer: readerB.token });
record("#4778", "odata/cross-tenant/tenant-b-reader-on-layer-10", "404 (tenant concealment)", crossBA, crossBA.status === 404 && crossBA.payloadRows === null);
const crossAB = await odata(11, { bearer: readerA.token });
record("#4778", "odata/cross-tenant/tenant-a-reader-on-layer-11", "404 (tenant concealment)", crossAB, crossAB.status === 404 && crossAB.payloadRows === null);

// Revocation: OData and feature-stream WebSocket (three consecutive sockets).
for (let attempt = 1; attempt <= 3; attempt++) {
  const credential = await issue(`replay-revoke-${attempt}`, "tenant-a");
  const before = await odata(10, { bearer: credential.token });
  const socket = await openFeatureSocket(credential, 10);
  await delay(1000);
  const revokedAt = await revoke(credential);
  const wsCell = webSocketBoundaryCell(`websocket/token-revocation/attempt-${attempt}`, socket, revokedAt);
  await delay(250);
  if (attempt === 1) {
    record("#4778", "odata/token-revocation/before", "200", before, before.status === 200);
    await odataChallengeCell("odata/token-revocation/after", () => odata(10, { bearer: credential.token }));
  }
  await wsCell;
}

// Expiry: the same credential on OData (polled across the advertised expiry) and
// on three feature-stream WebSockets. #4777: valid until expires, refused from it.
const expiring = await issue("replay-expiry", "tenant-a");
const expirySockets = [];
for (let i = 0; i < 3; i++) expirySockets.push(await openFeatureSocket(expiring, 10));
const expiryWs = expirySockets.map((s, i) => webSocketBoundaryCell(`websocket/token-expiry/socket-${i + 1}`, s, expiring.expires));
while (Date.now() < expiring.expires - 3000) await delay(250);
// A request validates somewhere between sending and receiving it: a refusal received
// before `expires` ended the token early, an admission sent after `expires` ended it late.
let last200 = null;
let first401 = null;
while (Date.now() < expiring.expires + 6000 && first401 === null) {
  const r = await odata(10, { bearer: expiring.token });
  if (r.status === 200) last200 = r;
  else if (r.status === 401) first401 = r;
  else { record("#4778", "odata/token-expiry/unexpected", "200 then 401", r, false); break; }
  await delay(100);
}
const edge = (r) => (r ? { sentMsFromExpiry: r.sentAt - expiring.expires, receivedMsFromExpiry: r.at - expiring.expires } : null);
record("#4777", "odata/token-expiry/valid-until-advertised-expiry",
  "no refusal received before expires, no admission sent after expires, refused within 5 s of expires",
  { lastAdmission: edge(last200), firstRefusal: edge(first401) },
  !!last200 && !!first401 && last200.sentAt <= expiring.expires && first401.at >= expiring.expires && first401.sentAt - expiring.expires <= 5000);
if (first401) record("#4778", "odata/token-expiry/after", "401 + WWW-Authenticate Bearer, no rows", first401, challenged(first401));
await Promise.all(expiryWs);

const descriptor = JSON.parse(fs.readFileSync(process.env.HONUA_REPLAY_DESCRIPTOR, "utf8"));
const failed = cells.filter((c) => c.result !== "passed");
fs.writeFileSync(output, `${JSON.stringify({
  format: "honua.live-auth-boundary-replay.v1",
  generatedAt: new Date().toISOString(),
  candidate: { image: descriptor.image, digest: descriptor.digest, revision: descriptor.revision, observedRevision: descriptor.observedRevision, deploymentFingerprint: descriptor.fingerprint, configuration: descriptor.configuration, seed: descriptor.seed, tenantOverlay: descriptor.tenantOverlay },
  summary: { cells: cells.length, passed: cells.length - failed.length, failed: failed.map((c) => c.cell) },
  cells,
}, null, 2)}\n`);
process.exit(failed.length === 0 ? 0 : 1);
