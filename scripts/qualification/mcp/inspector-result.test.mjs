import assert from 'node:assert/strict';
import test from 'node:test';
import { acceptInspectorResult, classifyInspectorFailure } from './inspector-result.mjs';

test('Inspector tool-error exit preserves a complete refusal but cannot accept a successful result', () => {
  const refusal = { isError: true, structuredContent: { code: 'invalid_argument' } };
  assert.deepEqual(acceptInspectorResult(JSON.stringify({ result: refusal }), 5), refusal);
  assert.throws(() => acceptInspectorResult('{"result":{"isError":false}}', 5), /inspector-invocation-failed/);
  assert.throws(() => acceptInspectorResult('{"result":{}}', 1), /inspector-invocation-failed/);
  assert.throws(() => acceptInspectorResult('{}', 0), /inspector-invocation-failed/);
});

test('Inspector diagnostics expose fixed classifications without upstream text', () => {
  const canary = 'private-key-or-provider-text';
  const stderr = JSON.stringify({ error: { status: 429, code: 'error', message: `rate limit ${canary}`, extra: canary } });
  const details = classifyInspectorFailure(stderr, { code: 1, killed: false }, {
    method: 'tools/call', view: 'full', extra: ['--tool-name', 'read'], stdout: '',
  });
  assert.equal(details.httpStatus, 429);
  assert.equal(details.clientErrorCode, 'error');
  assert.equal(details.rateLimitDiagnostic, true);
  assert.equal(details.tool, 'read');
  assert.equal(JSON.stringify(details).includes(canary), false);
});

test('Inspector rejects arbitrary status, error code and non-JSON diagnostics', () => {
  for (const stderr of ['not json', '{"error":{"status":"secret","code":"secret"}}', '{"error":{"status":999,"code":"secret"}}']) {
    const details = classifyInspectorFailure(stderr, { code: 'secret' }, {
      method: 'tools/list', view: 'default', extra: [], stdout: '',
    });
    assert.equal(details.httpStatus, undefined);
    assert.equal(details.clientErrorCode, undefined);
    assert.equal(details.exitCode, undefined);
    assert.equal(JSON.stringify(details).includes('secret'), false);
  }
});
