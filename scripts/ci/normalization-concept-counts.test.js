'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const crypto = require('node:crypto');
const { deriveConceptCountOutputs, updateProofCount } = require('./normalization-concept-counts');

const key = 'serve.geoservices-featureserver';
const path = `docs/okf/capabilities/${key}.md`;
const page = fs.readFileSync(path);
const matrixPath = 'docs/gis/data/capability-matrix.v1.json';
const matrix = counts => Buffer.from(JSON.stringify({ capabilities: counts.map(([key, provingTestCount]) => ({ key, provingTestCount })) }));

async function derive(previous, next, pages = { [path]: page }) {
  const reads = [];
  const outputs = [{ path: matrixPath, content_base64: matrix(next).toString('base64') }];
  const result = await deriveConceptCountOutputs({ outputs, readBlob: async (name, limit, required) => {
    reads.push({ name, limit, required });
    return name === matrixPath ? matrix(previous) : pages[name] ?? null;
  } });
  assert.equal(outputs.length, 1, 'derivation must not mutate the validated envelope');
  return { result, reads };
}

test('reported FeatureServer and geometry drift changes only the canonical numeric rows', async () => {
  const geometry = 'serve.geoservices-geometry-service';
  const geometryPath = `docs/okf/capabilities/${geometry}.md`;
  const geometryPage = fs.readFileSync(geometryPath);
  const { result } = await derive([[key, 597], [geometry, 144]], [[key, 599], [geometry, 147]], {
    [path]: page, [geometryPath]: geometryPage,
  });
  assert.equal(result.length, 2);
  for (const [index, original, before, after] of [[0, page, 597, 599], [1, geometryPage, 144, 147]]) {
    const output = result[index];
    const bytes = Buffer.from(output.content_base64, 'base64');
    assert.equal(bytes.toString(), original.toString().replace(`| Proving tests | ${before} |`, `| Proving tests | ${after} |`));
    assert.equal(output.length, bytes.length);
    assert.equal(output.sha256, crypto.createHash('sha256').update(bytes).digest('hex'));
  }
});

test('zero counts remove the row and restoring a count recreates exactly the original page', () => {
  const without = updateProofCount(page, key, 597, 0);
  assert.equal(without, page.toString().replace('| Proving tests | 597 |\n', ''));
  assert.equal(updateProofCount(Buffer.from(without), key, 0, 597), page.toString());
  assert.equal(updateProofCount(page, key, 597, 597), null);
});

test('already regenerated pages are idempotent', async () => {
  const updated = Buffer.from(page.toString().replace('| Proving tests | 597 |', '| Proving tests | 599 |'));
  const { result } = await derive([[key, 597]], [[key, 599]], { [path]: updated });
  assert.deepEqual(result, []);
});

test('unchanged, new and unpublished capabilities generate no pages', async () => {
  const { result, reads } = await derive([[key, 597], ['serve.unpublished', 1]], [[key, 597], ['serve.new', 2], ['serve.unpublished', 2]]);
  assert.deepEqual(result, []);
  assert.deepEqual(reads.map(read => read.name), [matrixPath, 'docs/okf/capabilities/serve.unpublished.md']);
  assert.equal(reads[0].required, true);
  assert.equal(reads[1].required, false);
});

test('malformed counts and unsafe keys fail before reading any concept page', async () => {
  for (const candidate of [[key, -1], [key, 1.5], [key, null], ['../README', 2], ['Serve.Other', 2], ['a'.repeat(121), 2]]) {
    await assert.rejects(derive([[key, 597]], [candidate]), /invalid or duplicate/);
  }
  await assert.rejects(derive([[key, 597]], [[key, 598], [key, 599]]), /duplicate/);
  await assert.rejects(derive([[key, 597]], Array.from({ length: 257 }, (_, i) => [`serve.test${i}`, 1])), /inventory/);
});

test('missing provenance, mismatched identity, ambiguous or unexpected counts fail closed', () => {
  for (const text of [
    page.toString().replace('GENERATED FILE', 'AUTHORED FILE'),
    page.toString().replace(`resource: "honua://capability/${key}"`, 'resource: "other"'),
    page.toString().replace(`| Capability key | \`${key}\` |`, '| Capability key | `other` |'),
    page.toString().replace('| Proving tests | 597 |', '| Proving tests | 596 |'),
    page.toString().replace('| Proving tests | 597 |', '| Proving tests | 597 |\n| Proving tests | 597 |'),
    page.toString().replace('| Proving tests | 597 |\n', ''),
  ]) assert.throws(() => updateProofCount(Buffer.from(text), key, 597, 599), /generated capability|unexpected/);
});

test('oversized and invalid UTF-8 blobs are rejected', async () => {
  assert.throws(() => updateProofCount(Buffer.alloc(65537, 65), key, 597, 599), /byte bound/);
  assert.throws(() => updateProofCount(Buffer.from([0xff]), key, 597, 599), /encoded data/);
  await assert.rejects(derive([[key, 597]], [[key, 599]], { [path]: Buffer.alloc(65537, 65) }), /byte bound/);
});

test('the aggregate derived-page limit is enforced', async () => {
  const pages = {};
  const before = [], after = [];
  for (let i = 0; i < 18; i++) {
    const name = `serve.test${i}`;
    before.push([name, 597]); after.push([name, 599]);
    pages[`docs/okf/capabilities/${name}.md`] = Buffer.from(page.toString().replaceAll(key, name) + 'x'.repeat(60000));
  }
  await assert.rejects(derive(before, after, pages), /aggregate byte bound/);
});
