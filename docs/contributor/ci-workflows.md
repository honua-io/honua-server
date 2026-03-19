# GitHub Actions Workflows

> **This document is superseded.** The canonical workflow inventory, gate model, and configuration conventions now live in [`docs/ci/`](../ci/):
>
> - [Workflow Inventory](../ci/workflow-inventory.md) — every workflow, its tier, triggers, and merge-blocking status
> - [Gate Model](../ci/gate-model.md) — the five-tier quality gate definitions and governing rules
> - [Config Conventions](../ci/config-conventions.md) — env vars, cache keys, artifact naming, secret names, and composite actions
>
> If docs and workflows disagree, trust `.github/workflows/*.yml`.

## Useful Commands

```bash
# List recent runs for CI
gh run list --workflow=ci.yml

# View logs for a run
gh run view <run-id> --log

# Manually start a workflow
gh workflow run load-soak-nightly.yml
```
