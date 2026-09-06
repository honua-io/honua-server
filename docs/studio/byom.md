# Bring your own model

The 2026.1 **preview** path keeps provider credentials in honua-server. Studio
calls `GET /api/v1/studio/ai/capabilities` and streams
`POST /api/v1/studio/ai/chat`; it does not call model vendors directly.

Set `StudioAiProxy:Enabled`, choose a default provider, and configure one or
more operator-named providers. Supported adapter kinds on the pinned candidate
are `anthropic`, `openai`, and `bedrock`. The `openai` kind accepts compatible
`/chat/completions` endpoints, including Ollama and gis-llm when it offers that
wire contract.

## Local Ollama

Start Ollama separately and pull a tool-capable model according to Ollama's
release documentation. From the server process, prove its endpoint is
reachable. `localhost` inside a container is the container itself, so use a
resolvable host or service name there.

Run `ollama list` and confirm that the tool-capable model you intend to configure
is present. This verifies the Ollama installation through its own CLI without
bypassing the supported client surface with a raw HTTP request.

Configure the candidate:

```bash
export StudioAiProxy__Enabled=true
export StudioAiProxy__DefaultProvider=ollama
export StudioAiProxy__Providers__ollama__Kind=openai
export StudioAiProxy__Providers__ollama__Endpoint=http://127.0.0.1:11434/v1
export StudioAiProxy__Providers__ollama__Model=qwen2.5:7b
```

No API key is required for a default local Ollama endpoint. Protect a remote
endpoint and supply its secret by environment variable or secret reference,
never browser configuration.

For Anthropic use `Kind=anthropic`, an HTTPS API base, a model, and a key or
secret reference. For hosted OpenAI-compatible services use `Kind=openai`,
their API base/model, and provider key. For Bedrock use `Kind=bedrock`, a model
ID and optional region; credentials come from the AWS credential chain.

Non-admin interactive users require
`Studio:EndUserAuthorization:Enabled=true`. API keys, client certificates, and
client-credentials tokens are not accepted as interactive Studio AI users.
The chat endpoint's application-side limit is 30 requests per minute when the
opt-in limiter is enabled with `RateLimiting__Enabled=true`; rate limiting is
off by default. If you leave it disabled, enforce an equivalent limit at your
WAF, API gateway, ingress, or load balancer.

The configuration was checked against candidate source, but no live Ollama
daemon/model was available in the candidate environment. This page therefore
does not claim a successful real-model turn. That receipt remains part of
[honua-studio#41](https://github.com/honua-io/honua-studio/issues/41).

## Signed certification transcripts

Release-certifying calls include a `certification` object with `candidateId`,
`releaseId`, `endpointIdentity`, `actionId`, and a unique `runNonce`. Only an
administrator can submit certification requests. A successful call emits a
`transcript_provenance` SSE event containing a detached Ed25519 signature and
the base64-encoded canonical transcript. Clients must wait for and verify this
event before accepting a successful turn or acting on a model-selected tool.
An HTTP 200 or a `message_stop` alone is insufficient certification evidence.

Configure signing separately from provider credentials:

```json
{
  "StudioAiProxy": {
    "TranscriptSigning": {
      "KeyId": "studio-transcript-current",
      "PrivateKeyReference": "secret://studio-ai/transcript-ed25519-seed",
      "LifetimeSeconds": 900
    }
  }
}
```

The reference must resolve through the server's secret provider to a
base64-encoded 32-byte Ed25519 seed. Keep that seed in server-controlled secret
storage; never place it in browser configuration, task environment variables,
Terraform inputs/state, logs, or receipts. Inline signing material and failed
secret resolution produce `studio_ai/provenance_signing_unavailable` before a
provider call. Provider or transcript-validation failures do not produce a
successful signed transcript.

The envelope uses schema `honua.studio-ai.transcript.v1`, canonicalization
`honua-canonical-json-v1`, and digest algorithm `sha-256`. Canonical JSON is
compact UTF-8 without a BOM: object properties use ordinal order, arrays retain
their order, strings use the server's deterministic JSON escaping, and numbers
are normalized without loss of precision. Duplicate properties are rejected.
The signature covers these fields:

- The five certification bindings, provider name, exact model identity, signer
  key ID, and issue/expiry timestamps.
- The accepted request's canonical bytes and canonical provider event bytes,
  both base64-encoded. Request whitespace and equivalent JSON encodings are
  normalized; this is not a signature over the original HTTP whitespace.
- The selected text response and the SHA-256 digest of the canonical event
  array as `terminalResultDigest`. Tool-call IDs, names, and assembled arguments
  are carried in that signed event array. This digest binds the model turn;
  successful downstream tool execution still needs its own execution receipt.

For a simple deterministic encoding vector, input `{"z":1.0,"a":2}` produces
the exact UTF-8 bytes `{"a":2,"z":1}`. `StudioAiTranscriptSignerTests` covers
nested ordering, duplicate rejection, escaped strings, and arbitrary-precision
numbers. `StudioAiCertificationEndpointTests` traverses the HTTP route,
production service, each provider adapter, and signer with controlled upstream
fixtures; it independently checks the expected request and response, signature,
event digest, all identity bindings, freshness, and post-signature mutations.
Those fixtures are not live provider/model qualification.

Publish public verification keys through the capabilities signing manifest and
pin its identity in the Studio/release verifier's trusted policy. Do not trust
a key merely because an untrusted receipt or endpoint supplies it. During
rotation, `TranscriptSigning:OverlapKeys` contains only public material:
`KeyId`, base64 `PublicKey`, both `NotBefore` and `NotAfter`, and `Revoked`.
Both window bounds are required, with `NotBefore` strictly before `NotAfter`.
Old receipts are acceptable only while the pinned policy permits that key,
the overlap and transcript validity windows apply, and all expected bindings
match; revoked, unknown, expired, or out-of-window keys fail closed.

Studio must verify every expected call before producing an intermediate pass;
honua-release independently checks the signature, canonical bytes, signer
policy, and candidate/release/endpoint/action/run/provider/model bindings.
Verification also rejects omitted fields, alternate envelope encodings, and
duplicate consumption. Maintain replay state in the trusted certifying run;
an unkeyed local digest or a valid signature alone does not prevent replay.
Cross-repo receipts and live calls for every release-certifying provider/model
remain required before closing
[honua-server#3424](https://github.com/honua-io/honua-server/issues/3424).
