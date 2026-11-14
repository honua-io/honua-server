import assert from 'node:assert/strict';
import test from 'node:test';
import { requiredInputs, verifyCatalogChannels } from './catalog-channel-contract.mjs';

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

test('a large refusal cannot substitute for exercising successful payload elision', async () => {
  const peer = fixture(result => ({ ...result, isError: true }));
  await assert.rejects(verifyCatalogChannels(peer), { message: 'size-boundary-not-exercised' });
});

test('documents nested required fields through arrays and schema variants', () => {
  const schema = { type: 'object', properties: { plan: { oneOf: [{
    type: 'object', required: ['steps'], properties: { steps: {
      type: 'array', description: 'Caller-authored steps.', items: { type: 'object', required: ['id'],
        properties: { id: { type: 'string', description: 'Caller-authored step id.' } } },
    } },
  }] } } };
  assert.deepEqual(requiredInputs(schema), [
    { field: 'plan.steps', description: 'Caller-authored steps.', source: 'Caller-authored steps.' },
    { field: 'plan.steps[].id', description: 'Caller-authored step id.', source: 'Caller-authored step id.' },
  ]);
  delete schema.properties.plan.oneOf[0].properties.steps.items.properties.id.description;
  assert.throws(() => requiredInputs(schema), { message: 'undocumented-required-input' });
});

test('roster diagnostics contain canonical missing names and counts without echoing unexpected server names', async () => {
  const peer = fixture();
  peer.discover = async () => [{ name: 'read' }, { name: 'untrusted-server-value' }];
  await assert.rejects(verifyCatalogChannels(peer), error => {
    assert.equal(error.message, 'unexpected-view-roster');
    assert.deepEqual(error.details, { view: 'setup', expectedCount: 2, actualCount: 2, missing: ['write'] });
    assert.equal(JSON.stringify(error.details).includes('untrusted-server-value'), false);
    return true;
  });
});

test('rejects a nonblank required description without a value origin', () => {
  const schema = { type: 'object', required: ['id'], properties: {
    id: { type: 'string', description: 'Dataset identifier.' },
  } };
  assert.throws(() => requiredInputs(schema), { message: 'missing-required-input-source' });
});

test('inherits a caller-owned object source while retaining each required field description', () => {
  const schema = { type: 'object', description: 'Caller-authored configuration.', required: ['id'], properties: {
    id: { type: 'string', description: 'Stable id.' },
  } };
  assert.deepEqual(requiredInputs(schema), [{ field: 'id', description: 'Stable id.', source: 'Caller-authored configuration.' }]);
});

test('records the named producer of a required value', () => {
  const schema = { type: 'object', required: ['jobId'], properties: {
    jobId: { type: 'string', description: 'Job id from honua_list_jobs.' },
  } };
  assert.equal(requiredInputs(schema)[0].source, 'Job id from honua_list_jobs.');
});
