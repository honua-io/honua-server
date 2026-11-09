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

export function requiredInputs(schema, path = '') {
  if (!schema || typeof schema !== 'object') return [];
  const inputs = (schema.required ?? []).map(name => {
    const description = schema.properties?.[name]?.description;
    requireContract(typeof description === 'string' && description.trim().length > 0, 'undocumented-required-input');
    return { field: path ? `${path}.${name}` : name, description };
  });
  for (const [name, child] of Object.entries(schema.properties ?? {})) {
    inputs.push(...requiredInputs(child, path ? `${path}.${name}` : name));
  }
  if (schema.items) inputs.push(...requiredInputs(schema.items, `${path}[]`));
  for (const keyword of ['allOf', 'anyOf', 'oneOf']) {
    for (const variant of schema[keyword] ?? []) inputs.push(...requiredInputs(variant, path));
  }
  return inputs;
}

export async function verifyCatalogChannels({ rosters, discover, call }) {
  const views = [];
  const inputInventory = [];
  let largestPayloadBytes = 0;
  let largestSuccessfulPayloadBytes = 0;
  for (const [view, expected] of Object.entries(rosters)) {
    const tools = await discover(view);
    const names = tools.map(tool => tool.name).sort();
    requireContract(expected.length > 0 && isDeepStrictEqual(names, [...expected].sort()), 'unexpected-view-roster');
    for (const tool of tools) {
      const inputs = requiredInputs(tool.inputSchema);
      if (view === 'full') inputInventory.push(...inputs.map(input => ({ tool: tool.name, ...input })));
    }
    let successes = 0;
    let refusals = 0;
    for (const name of names) {
      // These authenticated empty-argument calls cover reads and refusals.
      // Successful mutation semantics have separate workflow tests.
      const result = await call(view, name, {});
      const payloadBytes = requireMirroredPayload(result);
      largestPayloadBytes = Math.max(largestPayloadBytes, payloadBytes);
      if (result.isError === true) refusals++;
      else {
        successes++;
        largestSuccessfulPayloadBytes = Math.max(largestSuccessfulPayloadBytes, payloadBytes);
      }
    }
    views.push({ view, tools: names.length, successes, refusals });
  }
  requireContract(views.length > 0, 'empty-view-denominator');
  requireContract(largestSuccessfulPayloadBytes >= 1024, 'size-boundary-not-exercised');
  return { schema: 'honua.mcp-catalog-channel-contract/v1', views, largestPayloadBytes, largestSuccessfulPayloadBytes, inputInventory };
}
