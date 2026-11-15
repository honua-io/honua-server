'use strict';

const crypto = require('node:crypto');

const MATRIX_PATH = 'docs/gis/data/capability-matrix.v1.json';
const CONCEPT_PATH = /^docs\/okf\/capabilities\/[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*\.md$/;
const KEY = /^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$/;
const MAX_CAPABILITIES = 256;
const MAX_MATRIX_BYTES = 1024 * 1024;
const MAX_PAGE_BYTES = 64 * 1024;
const MAX_DERIVED_BYTES = 1024 * 1024;
const GENERATED = '<!-- GENERATED FILE - DO NOT EDIT. Regenerate with scripts/ci/generate-capability-concepts.py -->';
const FACTS = '\n\nThe facts above come from the server\'s capability registry and capability matrix,';

function boundedBytes(value, limit, label) {
  if (!Buffer.isBuffer(value) || value.length === 0 || value.length > limit) {
    throw new Error(`${label} exceeds its byte bound or is not blob data`);
  }
  return new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(value);
}

function proofCounts(bytes) {
  const matrix = JSON.parse(boundedBytes(bytes, MAX_MATRIX_BYTES, MATRIX_PATH));
  if (!Array.isArray(matrix.capabilities) || matrix.capabilities.length > MAX_CAPABILITIES) {
    throw new Error('capability matrix exceeds the bounded capability inventory');
  }
  const counts = new Map();
  for (const capability of matrix.capabilities) {
    const { key, provingTestCount: count } = capability;
    if (typeof key !== 'string' || key.length > 120 || !KEY.test(key) || counts.has(key)
        || !Number.isSafeInteger(count) || count < 0) {
      throw new Error('invalid or duplicate capability proof count');
    }
    counts.set(key, count);
  }
  return counts;
}

// Deliberately narrower than rendering arbitrary Markdown from contributor data:
// preserve every existing byte except the numeric proof row. Other documentation
// changes still require canonical generation by the author and ordinary gates.
function updateProofCount(bytes, key, { previous, next }) {
  const text = boundedBytes(bytes, MAX_PAGE_BYTES, key);
  if (!text.includes(`\n${GENERATED}\n`)
      || !text.includes(`\nresource: "honua://capability/${key}"\n`)
      || !text.includes(`\n| Capability key | \`${key}\` |\n`)) {
    throw new Error(`${key} is not an existing generated capability concept`);
  }
  const rows = [...text.matchAll(/^\| Proving tests \| ([0-9]+) \|$/gm)];
  if (rows.length > 1 || (!previous && rows.length && Number(rows[0][1]) !== next)
      || (previous && ((!rows.length && next !== 0)
        || (rows.length && ![previous, next].includes(Number(rows[0][1])))))) {
    throw new Error(`${key} has an unexpected proving-test row`);
  }
  if ((rows.length && Number(rows[0][1]) === next) || (!rows.length && next === 0)) return null;
  if (rows.length) {
    const row = rows[0];
    const end = row.index + row[0].length;
    const removeNewline = !next && text[end] === '\n' ? 1 : 0;
    return text.slice(0, row.index) + (next ? `| Proving tests | ${next} |` : '')
      + text.slice(end + removeNewline);
  }
  if (text.split(FACTS).length !== 2) throw new Error(`${key} has no unique generated facts boundary`);
  return text.replace(FACTS, `\n| Proving tests | ${next} |${FACTS}`);
}

async function deriveConceptCountOutputs({ outputs, readBlob }) {
  const matrix = outputs.find(output => output.path === MATRIX_PATH);
  if (!matrix) throw new Error('validated normalization plan has no capability matrix');
  const original = await readBlob(MATRIX_PATH, MAX_MATRIX_BYTES);
  if (original === null) throw new Error('capability matrix is absent from the exact PR tree');
  const next = proofCounts(Buffer.from(matrix.content_base64, 'base64'));
  const previous = proofCounts(original);
  const derived = [];
  let totalBytes = 0;
  for (const [key, count] of next) {
    // New/removed concepts and unpublished concepts remain the author's concern.
    if (!previous.has(key) || previous.get(key) === count) continue;
    const path = `docs/okf/capabilities/${key}.md`;
    const current = await readBlob(path, MAX_PAGE_BYTES);
    if (current === null) continue;
    const updated = updateProofCount(current, key, { previous: previous.get(key), next: count });
    if (updated === null) continue;
    const content = Buffer.from(updated, 'utf8');
    boundedBytes(content, MAX_PAGE_BYTES, path);
    totalBytes += content.length;
    if (totalBytes > MAX_DERIVED_BYTES) throw new Error('derived concept counts exceed aggregate byte bound');
    derived.push({
      path, length: content.length,
      sha256: crypto.createHash('sha256').update(content).digest('hex'),
      content_base64: content.toString('base64'),
    });
  }
  return derived;
}

module.exports = { CONCEPT_PATH, deriveConceptCountOutputs, updateProofCount };
