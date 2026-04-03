# MCP Certification Testing

This document covers the cross-repo testing setup for Honua's MCP surface.

In the public-interface proof ledger, MCP is the only sanctioned cross-repo child surface. `honua-server` owns seed data, CI wiring, and release-proof plumbing; `honua-sdk-js` owns the deterministic certification scripts, artifact generation, and LLM smoke implementation behind child ticket `#484`.

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
| Docs | honua-server | `docs/user/MCP_SERVER.md`, `docs/contributor/mcp-certification.md` |

## Seed data

MCP-specific test data lives in `tests/seed/mcp.yaml`. It follows the project's `version: 1` seed YAML format (SQL statement array) and is applied **after** the base CI seed.

### What the seed provides

| Requirement | Data |
|-------------|------|
| Multi-service listing | `test_service_mcp` service with layer 10 (layer 10 is also bound to `test_service` at order 1) |
| Polygon layer | Layer 10 (`Polygon` geometry type), 5 polygon features (objectids 1001–1005) |
| Point features for stats | 15 point features in layer 0 (objectids 10–24) with `count` (Integer) and `ratio` (Double) |
| Statistics fields | `count`: sum=725, min=10, max=100; `ratio`: sum=76.5, min=1.1, max=10.5 |
| Known spatial extent | All features within lon -122.50…-122.35, lat 37.70…37.84 (WGS84) |

### Updating seed data

1. Edit `tests/seed/mcp.yaml`.
2. Update the matching constants in `honua-sdk-js/mcp/test/certification/helpers/constants.ts` (references `mcp.yaml` line numbers).
3. Coordinate across both repos when changing expected values.

## CI jobs

> **Current status:** the `honua-server` CI jobs and seed data are landed, but the certification scripts (`test:certification`, `test:certification:artifact`, `test:llm-smoke`) are not yet present in `honua-sdk-js` `trunk`. Until those scripts are landed, the jobs skip with a CI warning annotation and produce no certification artifacts. See [Known gaps](#known-gaps).

### mcp-certification

- **Trigger:** push/PR to `trunk` + manual (`workflow_dispatch`), skips dependabot.
- **Matrix:** `transport: [grpc-web, rest]` — runs full suite in parallel per transport.
- **Steps:** checkout honua-server → shared setup (`.github/actions/setup-honua-mcp`) → `test:certification` → `test:certification:artifact` (generate report) → artifact upload.
- **Artifact:** `mcp-certification-{transport}`, 30-day retention.
- **Ref strategy:** the SDK ref is controlled by the `MCP_SDK_REF` env var at the top of `ci.yml` (currently `trunk`). While set to a branch name, certification runs are useful for development but the artifacts are **not reproducible release evidence** because the same server commit may exercise different SDK test code over time. For release-grade certification, pin `MCP_SDK_REF` to a specific tag or commit SHA. The CI job emits a warning annotation when the ref appears to be a branch name rather than a pinned ref. The `workflow_dispatch` `sdk_ref` input overrides the env var for one-off manual replays.
- **Script guard:** the `setup-honua-mcp` action inspects the SDK `package.json` for `test:certification`, `test:certification:artifact`, and `test:llm-smoke` **before** any heavy setup. If neither `test:certification` nor `test:llm-smoke` is found (e.g. SDK-side work not yet landed), the action skips the server build, database seed, and `npm ci` entirely; the job emits a warning annotation and completes without failure. Artifact generation and upload are also gated on the action's `cert-available` and `cert-artifact-available` outputs, so no artifacts are produced when certification was skipped or the artifact script is absent. If `test:certification` is present but `test:certification:artifact` is missing (partial SDK landing), the job emits a warning annotation noting that no evidence artifacts were produced — this ensures the release-evidence gap is visible even though the certification tests themselves passed.
- **Output:** exposes a `cert_ran` output (`true`/`false`) consumed by downstream jobs.

### mcp-llm-smoke

- **Depends on:** `mcp-certification` (only runs when `cert_ran == 'true'` — skipped entirely when certification scripts are absent).
- **`continue-on-error: true`** — failures annotate but do not gate.
- **Test runner skips scenarios** when `OPENAI_API_KEY` secret is empty or unset.
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

Provider: OpenAI `gpt-4o`, temperature 0, 30-second per-scenario timeout.

## Environment variables

| Variable | Used by | Required |
|----------|---------|----------|
| `HONUA_BASE_URL` | cert + smoke | Yes |
| `HONUA_TRANSPORT` | cert (matrix) + smoke (`rest`) | Yes |
| `HONUA_SERVICE_ID` | cert + smoke | Yes (default: `test_service`) |
| `HONUA_MCP_SERVICE_ID` | cert | Yes (default: `test_service_mcp`) |
| `HONUA_LAYER_ID` | cert + smoke | Yes (default: `0`) |
| `HONUA_MCP_LAYER_ID` | cert | Yes (default: `10`) |
| `HONUA_DEV_AUTH` | cert + smoke | No (default: `true` in CI) |
| `OPENAI_API_KEY` | smoke only | No (skip if unset) |

## Known gaps

- **Auth certification:** skipped when `HONUA_DEV_AUTH=true`. Full auth testing requires a separate CI lane with auth enforcement.
- **SDK scripts prerequisite:** CI jobs skip cleanly when `test:certification`, `test:certification:artifact`, or `test:llm-smoke` scripts are not present in the checked-out SDK ref. Land those scripts in `honua-sdk-js` before expecting certification results.
- **Cache-invalidation testing:** deferred until anonymous writes are available.
- **C# SDK interop lane:** deferred to follow-up issue.
- **Schema source consolidation:** `tests/seed/base-schema.sql` and `tests/python/shared/postgis.py` define CI and local-bootstrap catalog schemas independently. They already diverge on some columns. A future pass should have one consume the other to prevent further drift.
