---
type: index
title: "Demo runbooks"
description: "Entry point for the demo runbooks, which now live in honua-demo."
---
# Demo runbooks — moved

Recording and operator runbooks for the live demo deployment live in the
private demo-environment repository **honua-io/honua-demo** (`runbook/`),
alongside the demo's Terraform stack, seeds, and drift CI:

- `demo-b-ops-runbook.md` — Demo B (ops champion) recording runbook
- `demo-b-safe-rollback.md` — Demo B flagship safe layer-evolution /
  reversible-rollback sequence
- `scripts/demo-b-probes.sh`, `scripts/demo-b-safe-rollback.sh` — helper
  scripts for the above
- `demo-honua-io-capability-runbook.md` — full-capability operator runbook
  (pointer stub retained [here](demo-honua-io-capability-runbook.md))

They are operator documentation for a specific live deployment, so they live
with that deployment's infrastructure rather than in this public repository.

Schema-coupled demo seed SQL stays in this repo under `tests/seed/`
(validated against this repo's migrations) and is referenced from honua-demo by
pinned ref. Deterministic scene fixtures live under `tests/fixtures/scenes/` and
are documented by the tests that assert against them.
