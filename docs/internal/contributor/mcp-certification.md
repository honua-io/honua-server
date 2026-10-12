# MCP Certification Testing

This document covers the cross-repo testing setup for Honua's MCP surface.

In the public-interface proof ledger, MCP is a sanctioned cross-repo child surface. `honua-server` owns seed data, CI wiring, and release-proof plumbing; `honua-sdk-js` owns the deterministic certification scripts, artifact generation, and LLM smoke implementation behind child ticket `#484`.

## Repo ownership split

| Concern | Repo | Path |
|---------|------|------|
| Certification test code | honua-sdk-js | `mcp/test/certification/` |
| LLM smoke test code | honua-sdk-js | `mcp/test/llm-smoke/` |
| Artifact reporter | honua-sdk-js | `mcp/src/certification/cli.ts` |
| Base schema seed | honua-server | `tests/seed/base-schema.sql` |
| MCP seed data | honua-server | `tests/seed/mcp.yaml` |
| YAML seed applicator | honua-server | `tests/seed/apply-yaml-seed.sh` |
| CI jobs (cert + smoke) | honua-server | `.github/workflows/ci.yml` |
| Base CI setup action | honua-server | `.github/actions/setup-honua-server/action.yml` |
| MCP CI setup action | honua-server | `.github/actions/setup-honua-mcp/action.yml` |
| Docs | honua-server | `docs/developer/MCP_SERVER.md`, `docs/contributor/mcp-certification.md` |

## Seed data

MCP-specific test data lives in `tests/seed/mcp.yaml`. It follows the project's `version: 1` seed YAML format (SQL statement array) and is applied **after** the base CI seed.

### What the seed provides

| Requirement | Data |
|-------------|------|
| Multi-service listing | `test_service_mcp` service publishing layer `10` at binding order `0`; layer `10` is also bound to `test_service` at order `2`, after base layers `0` and `1` |
| Polygon layer | Storage layer 10 (`Polygon` geometry type), 5 polygon features (objectids 1001–1005) |
| Point features for stats | 15 point features in layer 0 (objectids 10–24) with `count` (Integer) and `ratio` (Double) |
| Statistics fields | `count`: sum=725, min=10, max=100; `ratio`: sum=76.5, min=1.1, max=10.5 |
| Known spatial extent | All features within lon -122.50…-122.35, lat 37.70…37.84 (WGS84) |

### Updating seed data

1. Edit `tests/seed/mcp.yaml`.
2. Update the explicit fixture environment in CI and the real PostgreSQL seed-sequence regression.
3. Coordinate across both repos when changing expected values.

## CI jobs

The `honua-server` CI jobs checkout `honua-sdk-js` at the pinned `MCP_SDK_REF`
from `.github/workflows/ci.yml` unless a manual `workflow_dispatch` run
overrides it. The setup action still guards script availability in the selected
SDK ref, so manual replays against older SDK commits skip cleanly with warning
annotations instead of failing before the server starts.

### mcp-certification

- **Trigger:** scheduled/manual full integration runs and PRs labeled `ci/full`, skips dependabot.
- **Matrix:** `transport: [grpc-web, rest]` — runs the direct-SDK MCP standalone surface against the actual seeded server. MCP itself uses an in-memory client/server transport; the matrix selects the SDK backend transport. This is distinct from native `/mcp` HTTP or stdio-proxy certification.
- **Steps:** checkout honua-server → shared setup (`.github/actions/setup-honua-mcp`) → install and build the exact selected SDK source → install its MCP file peer → `test:certification` → `test:certification:artifact` using the same live environment → verify actual mode, backend, transport, suite source and successful round trips → artifact upload.
- **Artifact:** `mcp-certification-{transport}`, 30-day retention.
- **Ref strategy:** the SDK ref is controlled by the `MCP_SDK_REF` env var at the top of `ci.yml`. When set to a branch name, certification runs are useful for development but the artifacts are **not reproducible release evidence** because the same server commit may exercise different SDK test code over time. For release-grade certification, pin `MCP_SDK_REF` to a specific tag or commit SHA (the current value is a pinned commit). The CI job emits a warning annotation when the ref appears to be a branch name rather than a pinned ref. The `workflow_dispatch` `sdk_ref` input overrides the env var for one-off manual replays.
- **Script guard:** the `setup-honua-mcp` action inspects the SDK `package.json` for `test:certification`, `test:certification:artifact`, and `test:llm-smoke` **before** any heavy setup. If neither `test:certification` nor `test:llm-smoke` is found (e.g. SDK-side work not yet landed), the action skips the server build, database seed, and `npm ci` entirely; the job emits a warning annotation and completes without failure. Artifact generation and upload are also gated on the action's `cert-available` and `cert-artifact-available` outputs, so no artifacts are produced when certification was skipped or the artifact script is absent. If `test:certification` is present but `test:certification:artifact` is missing (partial SDK landing), the job emits a warning annotation noting that no evidence artifacts were produced — this ensures the release-evidence gap is visible even though the certification tests themselves passed.
- **Layer ids:** `HONUA_MCP_LAYER_ID` is the service-local layer index returned by `honua_list_layers` for `test_service_mcp` (`10`). The Metadata V2 fixture explicitly publishes this index; it is not inferred from the binding order `0`.
- **Output:** exposes a `cert_ran` output (`true`/`false`) consumed by downstream jobs.

### mcp-llm-smoke

- **Depends on:** `mcp-certification` (only runs when `cert_ran == 'true'` — skipped entirely when certification scripts are absent).
- **`continue-on-error: true`** — failures annotate but do not gate.
- **Live model driver:** when `BEDROCK_AWS_ACCESS_KEY_ID` and `BEDROCK_AWS_SECRET_ACCESS_KEY` are configured, CI enables the SDK Bedrock driver with `HONUA_EVAL_BEDROCK=1`. The default model is `us.anthropic.claude-sonnet-4-5-20250929-v1:0` in `us-west-2`; override with repository variables `HONUA_MCP_SMOKE_BEDROCK_MODEL` and `HONUA_MCP_SMOKE_AWS_REGION`.
- **Fallback:** when Bedrock secrets are absent, the smoke lane runs without the live Bedrock driver and emits a notice. The job remains best-effort.
- **Artifact:** `mcp-llm-smoke-transcripts`, 30-day retention.

## How the seed is applied in CI

All integration-test jobs share a single base schema file (`tests/seed/base-schema.sql`) that creates the `honua` schema, core tables, indexes, and the default `test_service` + Point layer 0 and Mixed layer 1 with all field definitions plus deterministic baseline features. This file is applied first via `psql -f`.

MCP-specific data from `tests/seed/mcp.yaml` is then applied using the shared `tests/seed/apply-yaml-seed.sh` script:

```bash
python3 -m pip install 'pyyaml>=6,<7'
bash tests/seed/apply-yaml-seed.sh tests/seed/mcp.yaml
```

## Certification artifact format

**JSON** (`mcp-certification-results.json`):

```json
{
  "schemaVersion": 2,
  "protocol": {
    "targetMode": "standalone",
    "backend": "live",
    "honuaTransport": "grpc-web",
    "surface": "honua-mcp standalone → http://localhost:5000 (live public FeatureServer, no Honua surfaces)"
  },
  "provenance": { "suiteGitSha": "<exact selected SDK checkout>", "targetUrl": "<same live surface>" },
  "summary": { "pass": true, "failures": 0, "contractsFailed": 0, "contractsBlocked": 0 }
}
```

**Markdown** (`mcp-certification-results.md`): provenance, summary, tool conformance/round trips, contracts and explicit known gaps. The JSON also carries actual positive tool/contract counts; the gate rejects empty, offline, wrong-source and failed receipts.

## LLM smoke scenarios

| Scenario | Expected tool calls | Pass criteria |
|----------|-------------------|---------------|
| discover-and-describe | `honua_list_services` → `honua_describe_layer` | LLM calls both, answer mentions field names + geometry type |
| spatial-query | `honua_query_features` with filter | LLM calls query, answer contains feature names |
| statistics-workflow | `honua_statistics` | LLM calls statistics, answer contains numeric result |
| error-recovery | `honua_query_features` (bad layer) → recovery call | LLM handles error, successfully makes follow-up call |

Provider: deterministic control plus Claude via Amazon Bedrock when
`HONUA_EVAL_BEDROCK=1`; the Bedrock model and region are controlled by the CI
environment variables above.

## Environment variables

| Variable | Used by | Required |
|----------|---------|----------|
| `HONUA_MCP_CERT_TARGET` | cert | `standalone` with a real `HONUA_BASE_URL` |
| `HONUA_SUITE_GIT_SHA` | cert + smoke | Set from the actual checked-out SDK HEAD, not the enclosing server workflow SHA |
| `HONUA_BASE_URL` | cert + smoke | Yes |
| `HONUA_TRANSPORT` | cert (matrix) + smoke (`rest`) | Yes |
| `HONUA_SERVICE_ID` | cert + smoke | Yes (default: `test_service`) |
| `HONUA_MCP_SERVICE_ID` | cert | Yes (default: `test_service_mcp`) |
| `HONUA_LAYER_ID` | cert + smoke | Yes (default: `0`) |
| `HONUA_MCP_LAYER_ID` | cert | Yes (set by CI to `10`, the published layer id for `test_service_mcp`) |
| `HONUA_API_KEY` | cert | Seeded development admin credential, matching the server setup action |
| `BEDROCK_AWS_ACCESS_KEY_ID` / `BEDROCK_AWS_SECRET_ACCESS_KEY` | smoke only | No (enables live Bedrock driver when both are set) |
| `HONUA_MCP_SMOKE_BEDROCK_MODEL` | smoke only | No repository variable override for the Bedrock model |
| `HONUA_MCP_SMOKE_AWS_REGION` | smoke only | No repository variable override for the Bedrock region |

## Known gaps

For a bounded setup diagnosis, manually dispatch `mcp-certification-focused.yml`
on the reviewed server branch with the exact published MCP source SHA. Both
REST and grpc-web execute independently using the same setup action, test
commands, and live-receipt guard as full CI. Missing scripts fail this focused
workflow; startup logs and available receipts are retained even on failure.
This diagnostic is not a replacement for the complete `full_ci=true` matrix
required before an immutable server image can be published.

The server setup action starts the host in `Development` with `HONUA_ADMIN_PASSWORD`. The live SDK client uses the matching `HONUA_API_KEY`; it does not need `HONUA_DEV_AUTH`. Do not set that obsolete flag at job scope: the server inherits job variables and deliberately rejects development-auth bypasses outside `Test`.

- **Auth certification:** the direct-SDK standalone target has no MCP transport authentication boundary and records that limitation. Native server MCP authentication and governed operations require the separate remote/stdio and Production proof lanes.
- **Live certification prerequisite:** the default SDK source is immutable commit `584551c20dc059223814990d0a4964bbc7a5b8a2` ([SDK PR #2068](https://github.com/honua-io/honua-sdk-js/pull/2068)), which isolates certification settings and implements count/statistics over native gRPC. A source pin or workflow definition is not a pass: only actual successful live receipts for both backend transports qualify. The earlier candidate44c run failed fixture setup before tests; its failure remains historical evidence. [SDK issue #2067](https://github.com/honua-io/honua-sdk-js/issues/2067) remains open until both live backend runs pass.
- **LLM smoke availability:** this selected source has no `test:llm-smoke` script, so the best-effort smoke job explicitly skips and produces no model-evaluation evidence.
- **SDK scripts prerequisite for manual replays:** CI jobs skip cleanly when `test:certification`, `test:certification:artifact`, or `test:llm-smoke` scripts are not present in the checked-out SDK ref.
- **Cache-invalidation testing:** deferred until anonymous writes are available.
- **C# SDK interop lane:** deferred to follow-up issue.
- **Schema source consolidation:** `tests/seed/base-schema.sql` and `tests/python/shared/postgis.py` define CI and local-bootstrap catalog schemas independently. They already diverge on some columns. A future pass should have one consume the other to prevent further drift.
