#!/usr/bin/env node
import http from 'node:http';
import https from 'node:https';
import { readFile, writeFile } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import { createInterface } from 'node:readline';
import { pathToFileURL } from 'node:url';
import { performance, monitorEventLoopDelay } from 'node:perf_hooks';
import { setMaxListeners } from 'node:events';

const STREAM_PATH = '/api/v1/streaming/features?layers=0';
const EDIT_PATH = '/rest/services/test/FeatureServer/0/applyEdits';

function validate(profile) {
  for (const key of ['subscribers', 'editsPerSecond', 'seconds', 'settleMilliseconds']) {
    if (!Number.isSafeInteger(profile[key]) || profile[key] < (key === 'subscribers' ? 0 : 1)) {
      throw new Error(`Invalid ${key}`);
    }
  }
  if (!Array.isArray(profile.targets) || profile.targets.length !== 2) {
    throw new Error('Exactly two task targets are required');
  }
  for (const target of profile.targets) {
    const url = new URL(target.url);
    if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password) {
      throw new Error('Targets must use HTTP(S) without URL credentials');
    }
  }
  if (new URL(profile.targets[0].url).origin === new URL(profile.targets[1].url).origin) {
    throw new Error('Task targets must have distinct origins');
  }
  if (!Number.isSafeInteger(profile.seconds * profile.editsPerSecond)) {
    throw new Error('Edit count exceeds safe integer range');
  }
}

function request(target, path, options) {
  const url = new URL(path, target.url);
  const transport = url.protocol === 'https:' ? https : http;
  return transport.request(url, {
    ...options,
    headers: { ...target.headers, ...options.headers },
  });
}

function delay(milliseconds, signal) {
  return new Promise((resolve, reject) => {
    if (signal.aborted) return reject(signal.reason);
    const aborted = () => { clearTimeout(timer); reject(signal.reason); };
    const timer = setTimeout(() => { signal.removeEventListener('abort', aborted); resolve(); }, milliseconds);
    signal.addEventListener('abort', aborted, { once: true });
  });
}

function percentile(buckets, fraction) {
  const count = buckets.reduce((sum, value) => sum + value, 0);
  const rank = Math.ceil(count * fraction);
  let cumulative = 0;
  return buckets.findIndex(value => { cumulative += value; return rank > 0 && cumulative >= rank; });
}

function receiveFrame(state, frame, context) {
  if (frame.event === 'reset') throw new Error('Subscriber received reset');
  if (!frame.data.length) return;
  const data = JSON.parse(frame.data.join('\n'));
  if (frame.event === 'status' && data.status === 'error') throw new Error('Subscriber authorization or transport ended');
  if (frame.event !== 'feature-change') return;
  const name = data.attributes?.name;
  if (typeof name !== 'string' || !name.startsWith(`${context.marker}-`)) return;
  const suffix = name.slice(context.marker.length + 1);
  const ordinal = Number(suffix);
  if (!/^\d+$/.test(suffix) || !Number.isSafeInteger(ordinal) || ordinal >= context.total) {
    throw new Error('Invalid marked edit ordinal');
  }
  if (!Number.isSafeInteger(data.cursor) || frame.id !== `features:${data.cursor}`) {
    throw new Error('Invalid generation or event id');
  }
  if (data.cursor <= state.generation || state.seen[ordinal]) throw new Error('Duplicate or out-of-order delivery');
  const started = context.started[ordinal];
  if (!started) throw new Error('Received an edit before it was issued');
  state.generation = data.cursor;
  state.seen[ordinal] = 1;
  const latency = performance.now() - started;
  context.maximum = Math.max(context.maximum, latency);
  context.buckets[Math.min(60_000, Math.floor(latency))]++;
  if (++state.received === context.total) state.complete();
}

async function readFrames(response, state, context) {
  const lines = createInterface({ input: response, crlfDelay: Infinity });
  response.once('close', () => lines.close());
  let frame = { event: '', id: '', data: [] };
  for await (const line of lines) {
    if (line === '') {
      receiveFrame(state, frame, context);
      frame = { event: '', id: '', data: [] };
    } else if (line.startsWith('event:')) frame.event = line.slice(6).trimStart();
    else if (line.startsWith('id:')) frame.id = line.slice(3).trimStart();
    else if (line.startsWith('data:')) frame.data.push(line.slice(5).trimStart());
  }
  if (!context.stopping) throw new Error(`Stream ended after ${state.received} of ${context.total} edits`);
}

function openSubscriber(target, context) {
  const state = { generation: -1, seen: new Uint8Array(context.total), received: 0 };
  state.done = new Promise(resolve => { state.complete = resolve; });
  state.ready = new Promise((resolve, reject) => {
    const call = request(target, STREAM_PATH, {
      signal: context.abort.signal,
      headers: { Accept: 'text/event-stream' },
    });
    call.on('error', error => { reject(error); context.fail(error); });
    call.on('response', response => {
      if (response.statusCode !== 200 || !response.headers['content-type']?.startsWith('text/event-stream')) {
        response.resume();
        reject(new Error(`Stream admission failed: HTTP ${response.statusCode}`));
        return;
      }
      state.response = response;
      state.reading = readFrames(response, state, context).catch(context.fail);
      resolve();
    });
    call.end();
  });
  return state;
}

function edit(target, name, signal) {
  const body = JSON.stringify({ adds: [{ attributes: { name }, geometry: { x: -122.4194, y: 37.7749 } }] });
  return new Promise((resolve, reject) => {
    const call = request(target, EDIT_PATH, {
      method: 'POST', signal,
      headers: { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(body) },
    });
    call.on('error', reject);
    call.on('response', response => {
      let payload = '';
      response.setEncoding('utf8');
      response.on('error', reject);
      response.on('data', chunk => {
        payload += chunk;
        if (payload.length > 1_000_000) response.destroy(new Error('Oversized edit response'));
      });
      response.on('end', () => {
        try {
          if (response.statusCode !== 200 || JSON.parse(payload).addResults?.[0]?.success !== true) {
            throw new Error(`Edit failed: HTTP ${response.statusCode}`);
          }
          resolve();
        } catch (error) { reject(error); }
      });
    });
    call.end(body);
  });
}

async function writeAtRate(profile, context) {
  const pending = [];
  const start = performance.now();
  for (let ordinal = 0; ordinal < context.total; ordinal++) {
    const wait = start + ordinal * 1000 / profile.editsPerSecond - performance.now();
    if (wait > 0) await delay(wait, context.abort.signal);
    if (context.abort.signal.aborted) throw context.abort.signal.reason;
    context.started[ordinal] = performance.now();
    context.maximumScheduleLag = Math.max(context.maximumScheduleLag,
      context.started[ordinal] - start - ordinal * 1000 / profile.editsPerSecond);
    pending.push(edit(profile.targets[ordinal % 2], `${context.marker}-${ordinal}`, context.abort.signal).catch(context.fail));
  }
  await Promise.all(pending);
  return performance.now() - start;
}

export async function runScale(input) {
  const profile = { subscribers: 2000, editsPerSecond: 50, seconds: 600, settleMilliseconds: 60_000, ...input };
  validate(profile);
  const abort = new AbortController();
  setMaxListeners(0, abort.signal);
  // Native HTTP streams share no server thread pool. Run this process on a client host.
  const context = {
    abort, total: profile.seconds * profile.editsPerSecond, marker: `scale-${randomUUID()}`,
    buckets: new Float64Array(60_001), maximum: 0, maximumScheduleLag: 0, stopping: false,
    fail: error => { if (!context.stopping && !abort.signal.aborted) abort.abort(error); },
  };
  context.started = new Float64Array(context.total);
  const subscribers = [];
  const clientCpuStart = process.cpuUsage();
  const clientStarted = performance.now();
  const eventLoopDelay = monitorEventLoopDelay({ resolution: 20 });
  eventLoopDelay.enable();
  const timeout = setTimeout(() => context.fail(new Error('Scale run timed out')), (profile.seconds + 180) * 1000 + profile.settleMilliseconds);
  try {
    for (let i = 0; i < profile.subscribers; i++) {
      const subscriber = openSubscriber(profile.targets[i % 2], context);
      subscribers.push(subscriber);
      await subscriber.ready;
    }
    const writeMilliseconds = await writeAtRate(profile, context);
    const settle = setTimeout(() => context.fail(new Error('Delivery settle timeout')), profile.settleMilliseconds);
    try {
      await Promise.race([
        Promise.all(subscribers.map(subscriber => subscriber.done)),
        new Promise((_, reject) => {
          if (abort.signal.aborted) reject(abort.signal.reason);
          else abort.signal.addEventListener('abort', () => reject(abort.signal.reason), { once: true });
        }),
      ]);
    } finally { clearTimeout(settle); }
    await delay(500, abort.signal);
    if (abort.signal.aborted) throw abort.signal.reason;
    const p95 = percentile(context.buckets, 0.95);
    const clientCpu = process.cpuUsage(clientCpuStart);
    return {
      format: 'honua.realtime-scale-client.v1', marker: context.marker,
      subscribers: profile.subscribers, edits: context.total, seconds: profile.seconds,
      requestedEditsPerSecond: profile.editsPerSecond, writeMilliseconds,
      maximumScheduleLagMilliseconds: context.maximumScheduleLag,
      clientCpuCoreEquivalentPercent: (clientCpu.user + clientCpu.system) / ((performance.now() - clientStarted) * 10),
      clientEventLoopP95Milliseconds: eventLoopDelay.percentile(95) / 1_000_000,
      deliveries: subscribers.reduce((sum, subscriber) => sum + subscriber.received, 0),
      requestToEventP95Milliseconds: profile.subscribers ? p95 : null,
      requestToEventMaximumMilliseconds: profile.subscribers ? context.maximum : null,
      deliveryPassed: context.maximumScheduleLag <= 1000 && (!profile.subscribers || p95 <= 1000),
      scaleGatePassed: false,
      missingEvidence: ['per-task listener count and reconnects', 'database CPU delta against zero-subscriber baseline', 'independent task identity'],
    };
  } finally {
    context.stopping = true;
    eventLoopDelay.disable();
    clearTimeout(timeout);
    abort.abort();
    for (const subscriber of subscribers) subscriber.response?.destroy();
    await Promise.all(subscribers.map(subscriber => subscriber.reading));
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    if (process.argv.length !== 4) throw new Error('Usage: scale-client.mjs <private-profile.json> <result.json>');
    const result = await runScale(JSON.parse(await readFile(process.argv[2], 'utf8')));
    await writeFile(process.argv[3], `${JSON.stringify(result, null, 2)}\n`, { mode: 0o600 });
    console.log(`Delivery ${result.deliveryPassed ? 'PASS' : 'FAIL'}; scale gate remains incomplete`);
    if (!result.deliveryPassed) process.exitCode = 1;
  } catch (error) {
    console.error(`Scale client failed: ${error.message}`);
    process.exitCode = 1;
  }
}
