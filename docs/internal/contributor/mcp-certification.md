# MCP Certification Testing

This document covers the cross-repo testing setup for Honua's MCP surface.

In the public-interface proof ledger, MCP is a sanctioned cross-repo child surface. `honua-server` owns seed data, CI wiring, and release-proof plumbing; `honua-sdk-js` owns the deterministic certification scripts, artifact generation, and LLM smoke implementation behind child ticket `#484`.

## Repo ownership split

| Concern | Repo | Path |
|---------|------|------|
| Certification test code | honua-sdk-js | `mcp/test/certification/` |
| LLM smoke test code | honua-sdk-js | `mcp/test/llm-smoke/` |
| Artifact reporter | honua-sdk-js | `mcp/test/certification/helpers/artifact-reporter.ts` |
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
| Multi-service listing | `test_service_mcp` service with service-local layer `0`, backed by storage layer `10` (storage layer `10` is also bound to `test_service` at order 1) |
| Polygon layer | Storage layer 10 (`Polygon` geometry type), 5 polygon features (objectids 1001–1005) |
| Point features for stats | 15 point features in layer 0 (objectids 10–24) with `count` (Integer) and `ratio` (Double) |
| Statistics fields | `count`: sum=725, min=10, max=100; `ratio`: sum=76.5, min=1.1, max=10.5 |
| Known spatial extent | All features within lon -122.50…-122.35, lat 37.70…37.84 (WGS84) |

### Updating seed data

1. Edit `tests/seed/mcp.yaml`.
2. Update the matching constants in `honua-sdk-js/mcp/test/certification/helpers/constants.ts` (references `mcp.yaml` line numbers).
3. Coordinate across both repos when changing expected values.

## CI jobs

The `honua-server` CI jobs checkout `honua-sdk-js` at the pinned `MCP_SDK_REF`
from `.github/workflows/ci.yml` unless a manual `workflow_dispatch` run
overrides it. The setup action still guards script availability in the selected
SDK ref, so manual replays against older SDK commits skip cleanly with warning
annotations instead of failing before the server starts.

### mcp-certification

- **Trigger:** scheduled/manual full integration runs and PRs labeled `ci/full`, skips dependabot.
- **Matrix:** `transport: [grpc-web, rest]` — runs full suite in parallel per transport.
- **Steps:** checkout honua-server → shared setup (`.github/actions/setup-honua-mcp`) → `test:certification` → `test:certification:artifact` (generate report) → artifact upload.
- **Artifact:** `mcp-certification-{transport}`, 30-day retention.
- **Ref strategy:** the SDK ref is controlled by the `MCP_SDK_REF` env var at the top of `ci.yml`. When set to a branch name, certification runs are useful for development but the artifacts are **not reproducible release evidence** because the same server commit may exercise different SDK test code over time. For release-grade certification, pin `MCP_SDK_REF` to a specific tag or commit SHA (the current value is a pinned commit). The CI job emits a warning annotation when the ref appears to be a branch name rather than a pinned ref. The `workflow_dispatch` `sdk_ref` input overrides the env var for one-off manual replays.
- **Script guard:** the `setup-honua-mcp` action inspects the SDK `package.json` for `test:certification`, `test:certification:artifact`, and `test:llm-smoke` **before** any heavy setup. If neither `test:certification` nor `test:llm-smoke` is found (e.g. SDK-side work not yet landed), the action skips the server build, database seed, and `npm ci` entirely; the job emits a warning annotation and completes without failure. Artifact generation and upload are also gated on the action's `cert-available` and `cert-artifact-available` outputs, so no artifacts are produced when certification was skipped or the artifact script is absent. If `test:certification` is present but `test:certification:artifact` is missing (partial SDK landing), the job emits a warning annotation noting that no evidence artifacts were produced — this ensures the release-evidence gap is visible even though the certification tests themselves passed.
- **Layer ids:** `HONUA_MCP_LAYER_ID` is the service-local layer index returned by `honua_list_layers` for `test_service_mcp` (`0`), not the backing storage layer id (`10`).
- **Output:** exposes a `cert_ran` output (`true`/`false`) consumed by downstream jobs.

### mcp-llm-smoke

- **Depends on:** `mcp-certification` (only runs when `cert_ran == 'true'` — skipped entirely when certification scripts are absent).
- **`continue-on-error: true`** — failures annotate but do not gate.
- **Live model driver:** when `BEDROCK_AWS_ACCESS_KEY_ID` and `BEDROCK_AWS_SECRET_ACCESS_KEY` are configured, CI enables the SDK Bedrock driver with `HONUA_EVAL_BEDROCK=1`. The default model is `us.anthropic.claude-sonnet-4-5-20250929-v1:0` in `us-west-2`; override with repository variables `HONUA_MCP_SMOKE_BEDROCK_MODEL` and `HONUA_MCP_SMOKE_AWS_REGION`.
- **Fallback:** when Bedrock secrets are absent, the smoke lane runs without the live Bedrock driver and emits a notice. The job remains best-effort.
- **Artifact:** `mcp-llm-smoke-transcripts`, 30-day retention.

## How the seed is applied in CI

All integration-test jobs share a single base schema file (`tests/seed/base-schema.sql`) that creates the `honua` schema, core tables, indexes, and the default `test_service` + layer 0 with all field definitions plus deterministic baseline features. This file is applied first via `psql -f`.

MCP-specific data from `tests/seed/mcp.yaml` is then applied using the shared `tests/seed/apply-yaml-seed.sh` script:

```bash
python3 -m pip install 'pyyaml>=6,<7'
bash tests/seed/apply-yaml-seed.sh tests/seed/mcp.yaml
```

## Certification artifact format

**JSON** (`mcp-certification-results.json`):

```json
{
  "schemaVersion": 1,
  "transport": "grpc-web",
  "timestamp": "...",
  "durationMs": 8500,
  "summary": { "total": 48, "passed": 48, "failed": 0, "skipped": 0, "verdict": "PASS" },
  "tools": [{ "name": "honua_list_services", "tests": [...] }],
  "resources": [{ "uri": "honua://services", "tests": [...] }],
  "crossCutting": { "auth": { "tests": [...] }, "retry": {...} }
}
```

**Markdown** (`mcp-certification-results.md`): One row per area, columns: Area | Tests | Passed | Failed | Verdict.

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
| `HONUA_BASE_URL` | cert + smoke | Yes |
| `HONUA_TRANSPORT` | cert (matrix) + smoke (`rest`) | Yes |
| `HONUA_SERVICE_ID` | cert + smoke | Yes (default: `test_service`) |
| `HONUA_MCP_SERVICE_ID` | cert | Yes (default: `test_service_mcp`) |
| `HONUA_LAYER_ID` | cert + smoke | Yes (default: `0`) |
| `HONUA_MCP_LAYER_ID` | cert | Yes (default: `0`, service-local layer id for `test_service_mcp`) |
| `HONUA_DEV_AUTH` | cert + smoke | No (default: `true` in CI) |
| `BEDROCK_AWS_ACCESS_KEY_ID` / `BEDROCK_AWS_SECRET_ACCESS_KEY` | smoke only | No (enables live Bedrock driver when both are set) |
| `HONUA_MCP_SMOKE_BEDROCK_MODEL` | smoke only | No repository variable override for the Bedrock model |
| `HONUA_MCP_SMOKE_AWS_REGION` | smoke only | No repository variable override for the Bedrock region |

## Known gaps

- **Auth certification:** skipped when `HONUA_DEV_AUTH=true`. Full auth testing requires a separate CI lane with auth enforcement.
- **Certification not yet active (#2807):** the currently pinned `MCP_SDK_REF` does **not** contain the `test:certification`, `test:certification:artifact`, or `test:llm-smoke` scripts (they are tracked in `honua-sdk-js` `#484`). As a result the `mcp-certification` and `mcp-llm-smoke` lanes skip cleanly and **no certification artifact has been produced** — the MCP surface is not certified today. Do not cite the presence of these CI jobs as certification evidence. Once the scripts land, re-pin `MCP_SDK_REF` to the SDK commit that contains them so release-evidence runs actually execute and upload the `mcp-certification-{transport}` artifacts.
- **SDK scripts prerequisite for manual replays:** CI jobs skip cleanly when `test:certification`, `test:certification:artifact`, or `test:llm-smoke` scripts are not present in the checked-out SDK ref.
- **Cache-invalidation testing:** deferred until anonymous writes are available.
- **C# SDK interop lane:** deferred to follow-up issue.
- **Schema source consolidation:** `tests/seed/base-schema.sql` and `tests/python/shared/postgis.py` define CI and local-bootstrap catalog schemas independently. They already diverge on some columns. A future pass should have one consume the other to prevent further drift.
