# GitHub Actions Workflows

> **This document is superseded.** The canonical workflow inventory, gate model, and configuration conventions now live in [`docs/ci/`](../ci/):
>
> - [Workflow Inventory](../ci/workflow-inventory.md) — every workflow, its tier, triggers, and merge-blocking status
> - [Gate Model](../ci/gate-model.md) — the five-tier quality gate definitions and governing rules
> - [Config Conventions](../ci/config-conventions.md) — env vars, cache keys, artifact naming, secret names, and composite actions
>
> - [MCP Certification](mcp-certification.md) — cross-repo MCP certification testing, seed data, and CI jobs
>
> If docs and workflows disagree, trust `.github/workflows/*.yml`.

## MCP Certification

| Job (in `ci.yml`) | Purpose | Typical Trigger |
|---|---|---|
| `mcp-certification` | MCP tool/resource tests with fixed seed data against live Honua (matrix: `grpc-web`, `rest`; SDK ref floats to `trunk`) | PR + push to `trunk` + manual |
| `mcp-llm-smoke` | Non-blocking LLM smoke tests via OpenAI `gpt-4o` (runs after certification) | PR + push to `trunk` + manual |

See [MCP Certification](mcp-certification.md) for seed data, environment variables, and artifact details.

## Nightly/Scheduled Security

| Workflow File | Purpose |
|---|---|
| `security-nightly.yml` | Dependency/security scanning |
| `trivy-nightly.yml` | Nightly Trivy scan |
| `nightly-container-build.yml` | Nightly container build checks |

## Useful Commands

```bash
# List recent runs for CI
gh run list --workflow=ci.yml

# View logs for a run
gh run view <run-id> --log

# Manually start a workflow
gh workflow run load-soak-nightly.yml
```
