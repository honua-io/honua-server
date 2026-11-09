import { isDeepStrictEqual } from 'node:util';

export class CatalogContractFailure extends Error {}

function requireContract(condition, code) {
  if (!condition) throw new CatalogContractFailure(code);
}

export function requireMirroredPayload(result) {
  requireContract(result?.structuredContent && typeof result.structuredContent === 'object', 'missing-structured-content');
  requireContract(Array.isArray(result.content), 'missing-content');
  const mirrors = result.content.filter(block => {
    if (block.type !== 'text' || typeof block.text !== 'string') return false;
    try { return isDeepStrictEqual(JSON.parse(block.text), result.structuredContent); }
    catch { return false; }
  });
  requireContract(mirrors.length === 1, 'missing-or-duplicate-payload-mirror');
  return Buffer.byteLength(JSON.stringify(result.structuredContent));
}

export async function verifyCatalogChannels({ rosters, discover, call }) {
  const views = [];
  let largestPayloadBytes = 0;
  for (const [view, expected] of Object.entries(rosters)) {
    const tools = await discover(view);
    const names = tools.map(tool => tool.name).sort();
    requireContract(expected.length > 0 && isDeepStrictEqual(names, [...expected].sort()), 'unexpected-view-roster');
    let successes = 0;
    let refusals = 0;
    for (const name of names) {
      // These authenticated empty-argument calls cover reads and refusals.
      // Successful mutation semantics have separate workflow tests.
      const result = await call(view, name, {});
      largestPayloadBytes = Math.max(largestPayloadBytes, requireMirroredPayload(result));
      if (result.isError === true) refusals++;
      else successes++;
    }
    views.push({ view, tools: names.length, successes, refusals });
  }
  requireContract(views.length > 0, 'empty-view-denominator');
  requireContract(largestPayloadBytes >= 1024, 'size-boundary-not-exercised');
  return { schema: 'honua.mcp-catalog-channel-contract/v1', views, largestPayloadBytes };
}
