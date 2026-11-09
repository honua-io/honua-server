import { isDeepStrictEqual } from 'node:util';

export class CatalogContractFailure extends Error {
  constructor(code, details) {
    super(code);
    this.details = details;
  }
}

function requireContract(condition, code, details) {
  if (!condition) throw new CatalogContractFailure(code, details);
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

function valueOrigin(description, inherited) {
  return typeof description === 'string' && /\bcaller\b|\bhonua_[a-z0-9_]+\b/i.test(description) ? description : inherited;
}

export function requiredInputs(schema, path = '', inheritedOrigin) {
  if (!schema || typeof schema !== 'object') return [];
  const origin = valueOrigin(schema.description, inheritedOrigin);
  const inputs = (schema.required ?? []).map(name => {
    const description = schema.properties?.[name]?.description;
    requireContract(typeof description === 'string' && description.trim().length > 0, 'undocumented-required-input');
    const source = valueOrigin(description, origin);
    requireContract(typeof source === 'string' && source.trim().length > 0, 'missing-required-input-source');
    return { field: path ? `${path}.${name}` : name, description, source };
  });
  for (const [name, child] of Object.entries(schema.properties ?? {})) {
    inputs.push(...requiredInputs(child, path ? `${path}.${name}` : name, origin));
  }
  if (schema.items) inputs.push(...requiredInputs(schema.items, `${path}[]`, origin));
  for (const keyword of ['allOf', 'anyOf', 'oneOf']) {
    for (const variant of schema[keyword] ?? []) inputs.push(...requiredInputs(variant, path, origin));
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
    requireContract(expected.length > 0 && isDeepStrictEqual(names, [...expected].sort()), 'unexpected-view-roster', {
      view, expectedCount: expected.length, actualCount: names.length,
      missing: expected.filter(name => !names.includes(name)),
    });
    for (const tool of tools) {
      const inputs = requiredInputs(tool.inputSchema);
      if (view === 'full') inputInventory.push(...inputs.map(input => ({ tool: tool.name, ...input })));
    }
    let successes = 0;
    let refusals = 0;
    for (const name of names) {
      // Empty arguments exercise reads and refusal payloads without supplying mutation inputs.
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
