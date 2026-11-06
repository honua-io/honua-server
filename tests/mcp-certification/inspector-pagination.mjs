// Transparent observation of the public installed Inspector client. No MCP
// request/response, page or cursor is manufactured by this helper.
import { createServer, request as httpRequest } from 'node:http';
import { request as httpsRequest } from 'node:https';
import { createHash } from 'node:crypto';
import { gunzipSync, inflateSync, brotliDecompressSync } from 'node:zlib';

export const INSPECTOR_PAGINATION_CONTRACT = 'honua.inspector-pagination-observer/v2';
// The candidate's real resources/list fits on one default page, so it cannot
// prove cursor traversal. The authenticated explicit full tools/list export
// spans several real default pages; observe that chain without changing sizes.
export const INSPECTOR_PAGINATION_METHOD = 'tools/list';
export const INSPECTOR_PAGINATION_VIEW = 'full';
const VIEW_KEY = 'honua.io/workflow-view';
const LIMIT = 4 * 1024 * 1024;
const sha256 = (value) => 'sha256:' + createHash('sha256').update(value).digest('hex');
const fail = () => { throw new Error('Inspector pagination wire proof failed.'); };

function decodedBody(page) {
  const encoding = page.contentEncoding.trim().toLowerCase();
  if (!encoding || encoding === 'identity') return page.bytes;
  const decode = encoding === 'gzip' ? gunzipSync : encoding === 'deflate' ? inflateSync
    : encoding === 'br' ? brotliDecompressSync : undefined;
  if (!decode) fail();
  return decode(page.bytes, { maxOutputLength: LIMIT });
}

function responseFor(bytes, contentType, id) {
  let frames;
  if (contentType.startsWith('application/json')) frames = [JSON.parse(bytes)];
  else if (contentType.startsWith('text/event-stream')) frames = bytes.toString().split(/\r?\n\r?\n/)
    .map((event) => event.split(/\r?\n/).filter((line) => line.startsWith('data:')).map((line) => line.slice(5).trimStart()).join('\n'))
    .filter(Boolean).map((data) => JSON.parse(data));
  else fail();
  const matched = frames.filter((frame) => frame.id === id);
  if (matched.length !== 1 || matched[0].jsonrpc !== '2.0' || matched[0].error !== undefined
    || !matched[0].result || typeof matched[0].result !== 'object') fail();
  return matched[0].result;
}

function prove(pages, output, inspectorVersion, sourceSha) {
  if (!/^2\.\d+\.\d+$/.test(inspectorVersion) || !/^[a-f0-9]{40}$/.test(sourceSha)
    || pages.length < 2 || pages.length > 100) fail();
  const seen = new Set();
  const names = [];
  const ids = new Set();
  let expected;
  let decodedBytes = 0;
  const session = pages[0].session;
  for (const [position, page] of pages.entries()) {
    if (!page.complete || page.failed || page.status !== 200 || page.session !== session
      || !['string', 'number'].includes(typeof page.id) || ids.has(`${typeof page.id}:${page.id}`)
      || page.cursor !== expected) fail();
    ids.add(`${typeof page.id}:${page.id}`);
    const bytes = decodedBody(page);
    if (bytes.length > LIMIT || (decodedBytes += bytes.length) > 4 * LIMIT) fail();
    if (page.view !== INSPECTOR_PAGINATION_VIEW || page.viewOverride || !page.authenticated) fail();
    const result = responseFor(bytes, page.contentType, page.id);
    if (!Array.isArray(result.tools) || result.tools.length === 0) fail();
    for (const tool of result.tools) {
      if (!tool || typeof tool.name !== 'string' || !tool.name || names.includes(tool.name)) fail();
      names.push(tool.name);
    }
    const next = result.nextCursor;
    if (next !== undefined) {
      if (typeof next !== 'string' || !next || seen.has(next) || position === pages.length - 1) fail();
      seen.add(next);
    } else if (position !== pages.length - 1) fail();
    expected = next;
  }
  const returned = output?.result ?? output;
  if (!Array.isArray(returned?.tools)
    || JSON.stringify(returned.tools.map((item) => item?.name)) !== JSON.stringify(names)) fail();
  return { schema: INSPECTOR_PAGINATION_CONTRACT, canonicalClient: 'MCP Inspector', inspectorVersion,
    sourceSha, method: INSPECTOR_PAGINATION_METHOD, workflowView: INSPECTOR_PAGINATION_VIEW, authenticated: true,
    pageCount: pages.length, cursorHandoffCount: seen.size, toolCount: names.length,
    toolNamesSha256: sha256(JSON.stringify(names)), sessionSha256: sha256(session ?? 'stateless'),
    complete: true, clientResultMatchesWire: true };
}

export async function observeInspectorPagination({ endpoint, invoke, inspectorVersion, sourceSha }) {
  const target = new URL(endpoint);
  if (!['http:', 'https:'].includes(target.protocol) || target.username || target.password || target.search
    || target.hash || target.pathname !== '/mcp' || typeof invoke !== 'function') fail();
  const pages = [];
  const active = new Set();
  let rejectDeadline;
  const expired = new Promise((_, reject) => { rejectDeadline = reject; });
  const deadline = setTimeout(() => {
    for (const request of active) request.destroy();
    rejectDeadline(new Error('Inspector pagination observation deadline exceeded.'));
  }, 30_000);
  let invalid = false;
  let retainedBytes = 0;
  const proxy = createServer(async (incoming, outgoing) => {
    let request;
    try {
      if (incoming.url !== '/mcp' || !['GET', 'POST', 'DELETE'].includes(incoming.method)) fail();
      const chunks = [];
      let length = 0;
      for await (const chunk of incoming) {
        if ((length += chunk.length) > LIMIT) fail();
        chunks.push(chunk);
      }
      const body = Buffer.concat(chunks);
      const message = incoming.method === 'POST' ? JSON.parse(body) : undefined;
      // Only presence of a credential is retained; its value never leaves the request.
      const credential = [incoming.headers['x-api-key'], incoming.headers.authorization]
        .some((value) => typeof value === 'string' && value.trim() !== '');
      const page = message?.method === INSPECTOR_PAGINATION_METHOD ? {
        id: message.id, cursor: message.params?.cursor, session: incoming.headers['mcp-session-id'],
        view: message.params?._meta?.[VIEW_KEY], viewOverride: message.params?.view !== undefined,
        authenticated: credential,
        complete: false, failed: false, contentType: '', contentEncoding: '', bytes: Buffer.alloc(0),
      } : undefined;
      if (page) { pages.push(page); if (pages.length > 100) fail(); }
      const headers = { ...incoming.headers, host: target.host };
      delete headers.connection;
      request = (target.protocol === 'https:' ? httpsRequest : httpRequest)(target, {
        method: incoming.method, headers, timeout: 25_000,
      }, (response) => {
        if (response.statusCode >= 300 && response.statusCode < 400) { invalid = true; response.destroy(); outgoing.writeHead(502).end(); return; }
        if (page) {
          page.status = response.statusCode; page.contentType = response.headers['content-type'] ?? '';
          page.contentEncoding = response.headers['content-encoding'] ?? '';
        }
        outgoing.writeHead(response.statusCode, response.headers);
        let size = 0; const bytes = [];
        response.on('data', (chunk) => {
          if (page) {
            size += chunk.length;
            if (size > LIMIT || (retainedBytes += chunk.length) > 4 * LIMIT) { invalid = true; page.failed = true; response.destroy(); return; }
            bytes.push(chunk);
          }
        });
        response.on('end', () => {
          if (page) { page.bytes = Buffer.concat(bytes); page.complete = true; }
        });
        response.on('error', () => { invalid = true; if (page) page.failed = true; outgoing.destroy(); });
        response.pipe(outgoing);
      });
      active.add(request);
      request.on('close', () => active.delete(request));
      request.on('timeout', () => request.destroy());
      request.on('error', () => { invalid = true; if (page) page.failed = true; outgoing.destroy(); });
      outgoing.on('close', () => request.destroy());
      request.end(body);
    } catch {
      invalid = true;
      request?.destroy();
      if (!outgoing.headersSent) outgoing.writeHead(502).end(); else outgoing.destroy();
    }
  });
  try {
    await new Promise((resolve, reject) => { proxy.once('error', reject); proxy.listen(0, '127.0.0.1', resolve); });
    const output = await Promise.race([invoke(`http://127.0.0.1:${proxy.address().port}/mcp`), expired]);
    if (invalid) fail();
    return prove(pages, output, inspectorVersion, sourceSha);
  } catch {
    fail();
  } finally {
    clearTimeout(deadline);
    for (const request of active) request.destroy();
    proxy.closeAllConnections();
    await new Promise((resolve) => proxy.close(resolve));
  }
}
