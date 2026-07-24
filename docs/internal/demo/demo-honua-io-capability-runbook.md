# Demo operator runbook — moved

This runbook moved to the private demo-environment repository:
**honua-io/honua-demo** (`runbook/demo-honua-io-capability-runbook.md`),
alongside the demo's Terraform stack, seeds, and drift CI (extraction:
honua-io/honua-iac#126).

It is operator documentation for a specific live deployment, so it lives with
that deployment's infrastructure rather than in this public repository.
Schema-coupled demo seed SQL remains here under `tests/seed/` (validated
against this repo's migrations) and is referenced from honua-demo by pinned
ref.
