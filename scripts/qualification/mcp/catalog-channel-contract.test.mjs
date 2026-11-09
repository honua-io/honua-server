import assert from 'node:assert/strict';
import test from 'node:test';
import { verifyCatalogChannels } from './catalog-channel-contract.mjs';

function fixture(change = result => result) {
  const calls = [];
  return {
    calls,
    rosters: { setup: ['read', 'write'], configure: ['read'] },
    discover: async view => (view === 'setup' ? ['write', 'read'] : ['read']).map(name => ({ name })),
    call: async (view, name, args) => {
      calls.push({ view, name, args });
      const structuredContent = name === 'read' ? { data: 'x'.repeat(2048) } : { code: 'invalid_argument', message: 'sourceId from honua_list_jobs is required.' };
      return change({ isError: name === 'write', structuredContent, content: [
        { type: 'text', text: 'Human summary' },
        { type: 'text', text: JSON.stringify(structuredContent) },
      ] });
    },
  };
}

test('calls every tool in every view and counts successful reads and refusals honestly', async () => {
  const peer = fixture();
  const receipt = await verifyCatalogChannels(peer);
  assert.deepEqual(peer.calls, [
    { view: 'setup', name: 'read', args: {} },
    { view: 'setup', name: 'write', args: {} },
    { view: 'configure', name: 'read', args: {} },
  ]);
  assert.deepEqual(receipt.views, [
    { view: 'setup', tools: 2, successes: 1, refusals: 1 },
    { view: 'configure', tools: 1, successes: 1, refusals: 0 },
  ]);
  assert.ok(receipt.largestPayloadBytes > 2048);
});

for (const [name, mutate, code] of [
  ['missing structured channel', result => ({ ...result, structuredContent: undefined }), 'missing-structured-content'],
  ['missing content channel', result => ({ ...result, content: undefined }), 'missing-content'],
  ['summary-only elision', result => ({ ...result, content: result.content.slice(0, 1) }), 'missing-or-duplicate-payload-mirror'],
  ['changed text payload', result => ({ ...result, content: [{ type: 'text', text: '{"changed":true}' }] }), 'missing-or-duplicate-payload-mirror'],
  ['duplicate JSON mirrors', result => ({ ...result, content: [...result.content, result.content[1]] }), 'missing-or-duplicate-payload-mirror'],
]) {
  test(`rejects ${name}`, async () => {
    await assert.rejects(verifyCatalogChannels(fixture(mutate)), { message: code });
  });
}

test('rejects missing, duplicate or wrong-view tools against the canonical denominator', async () => {
  for (const names of [['read'], ['read', 'read'], ['read', 'other']]) {
    const peer = fixture();
    peer.discover = async () => names.map(name => ({ name }));
    await assert.rejects(verifyCatalogChannels(peer), { message: 'unexpected-view-roster' });
  }
});

test('rejects an empty denominator and a run without a large result', async () => {
  await assert.rejects(verifyCatalogChannels({ rosters: {} }), { message: 'empty-view-denominator' });
  const peer = fixture(() => ({ structuredContent: {}, content: [{ type: 'text', text: '{}' }] }));
  await assert.rejects(verifyCatalogChannels(peer), { message: 'size-boundary-not-exercised' });
});
