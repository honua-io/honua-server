// Bedrock AI triage for genuinely-real CI failures (#2021).
//
// Given a failing job's name + log tail, ask Claude on Amazon Bedrock (Converse API,
// AWS credential chain) for a structured triage verdict:
//   { classification: "flake" | "real", rootCause, suggestedAction }
//
// Auth: the standard AWS credential chain via @aws-sdk/client-bedrock-runtime
// (env vars set by aws-actions/configure-aws-credentials, or an attached role).
// NO API key. The model is a Bedrock model id / cross-region inference profile.
//
// GRACEFUL DEGRADATION: this module never throws to the caller for a missing-creds
// or missing-SDK condition. `runTriage()` returns a result object with
// `available: false` and a human-readable `notice` instead, so the workflow can
// still label the PR and post a "triage skipped" comment rather than hard-failing.
// It only surfaces a hard error for an unexpected Bedrock fault AFTER creds were
// present (the caller decides whether to treat that as fatal — the workflow does not).

'use strict';

const DEFAULT_MODEL = 'us.anthropic.claude-sonnet-4-5-20250929-v1:0';
const DEFAULT_REGION = 'us-west-2';
const MAX_LOG_CHARS = 12000; // keep the prompt bounded; tail is the informative part

function buildPrompt({ jobName, prNumber, logTail }) {
  const tail = (logTail || '').slice(-MAX_LOG_CHARS);
  return [
    'You are a CI triage assistant for the honua-server repository.',
    'A CI job failed AND it was NOT auto-classified as a known transient flake',
    '(it is either a deterministic gate — Build/Format/Analyze/AOT/Package/CodeQL —',
    'or an integration shard that failed across a rerun). Decide whether this is a',
    'genuine code/logic break ("real") or still likely a transient infrastructure',
    'flake ("flake") that escaped the deterministic classifier (e.g. a Postgres',
    '40P01 deadlock, a Testcontainers/Docker startup hiccup, a runner OOM, a network',
    'blip, or a cancelled job).',
    '',
    `PR: #${prNumber}`,
    `Failing job: ${jobName}`,
    '',
    'Failing job log tail (most recent output):',
    '```',
    tail,
    '```',
    '',
    'Respond with ONLY a JSON object, no prose, of the exact shape:',
    '{"classification":"real"|"flake","rootCause":"<one or two sentences>",',
    '"suggestedAction":"<one concrete next step: e.g. rerun the shard, fix X in file Y, bump timeout>"}',
  ].join('\n');
}

function parseVerdict(text) {
  // The model is asked for bare JSON, but be defensive: pull the first {...} block.
  const match = (text || '').match(/\{[\s\S]*\}/);
  if (!match) {
    return { classification: 'unknown', rootCause: 'Model returned no parseable JSON.', suggestedAction: 'Inspect the failing job logs manually.', raw: text };
  }
  try {
    const obj = JSON.parse(match[0]);
    return {
      classification: obj.classification === 'flake' ? 'flake' : (obj.classification === 'real' ? 'real' : 'unknown'),
      rootCause: String(obj.rootCause || '').slice(0, 1000),
      suggestedAction: String(obj.suggestedAction || '').slice(0, 1000),
      raw: text,
    };
  } catch {
    return { classification: 'unknown', rootCause: 'Model JSON failed to parse.', suggestedAction: 'Inspect the failing job logs manually.', raw: text };
  }
}

/**
 * @returns {Promise<{available: boolean, notice?: string, classification?: string,
 *                    rootCause?: string, suggestedAction?: string, model?: string}>}
 */
async function runTriage({ jobName, prNumber, logTail, model, region }) {
  const resolvedModel = model || process.env.BEDROCK_TRIAGE_MODEL || DEFAULT_MODEL;
  const resolvedRegion = region || process.env.AWS_REGION || process.env.BEDROCK_TRIAGE_REGION || DEFAULT_REGION;

  // Cheap pre-check: if no AWS credential signal is present at all, skip without
  // touching the SDK. configure-aws-credentials exports AWS_ACCESS_KEY_ID (static)
  // or AWS_ROLE_ARN/AWS_WEB_IDENTITY_TOKEN_FILE (OIDC) or container creds.
  const hasCredSignal =
    process.env.AWS_ACCESS_KEY_ID ||
    process.env.AWS_ROLE_ARN ||
    process.env.AWS_WEB_IDENTITY_TOKEN_FILE ||
    process.env.AWS_CONTAINER_CREDENTIALS_RELATIVE_URI ||
    process.env.AWS_PROFILE;
  if (!hasCredSignal) {
    return {
      available: false,
      notice: 'No AWS credentials available in CI (no BEDROCK OIDC role / keys configured) — AI triage skipped. See docs/internal/contributor/merge-coordination-runbook.md to enable.',
      model: resolvedModel,
    };
  }

  let BedrockRuntimeClient, ConverseCommand;
  try {
    ({ BedrockRuntimeClient, ConverseCommand } = require('@aws-sdk/client-bedrock-runtime'));
  } catch {
    return {
      available: false,
      notice: '@aws-sdk/client-bedrock-runtime is not installed on the runner — AI triage skipped (the PR is still labelled).',
      model: resolvedModel,
    };
  }

  const client = new BedrockRuntimeClient({ region: resolvedRegion });
  const prompt = buildPrompt({ jobName, prNumber, logTail });

  const resp = await client.send(new ConverseCommand({
    modelId: resolvedModel,
    messages: [{ role: 'user', content: [{ text: prompt }] }],
    inferenceConfig: { maxTokens: 1024, temperature: 0 },
  }));

  const text = (resp.output && resp.output.message && resp.output.message.content || [])
    .map(b => b.text || '')
    .join('');
  const verdict = parseVerdict(text);
  return { available: true, model: resolvedModel, ...verdict };
}

module.exports = { runTriage, buildPrompt, parseVerdict, DEFAULT_MODEL, DEFAULT_REGION };
