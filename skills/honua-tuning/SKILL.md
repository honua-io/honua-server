---
name: honua-tuning
description: Use when a Honua operator needs to diagnose slow layer publication and adjust documented materialization budgets without changing query or approval policy.
---

# Tune a measured Honua operation

Read the install's `AGENTS.md`. Establish which stage is slow: import, canonical
snapshot materialization, feature query, or map rendering. Inspect bounded,
redacted job/status evidence before changing settings. A caller timeout alone
does not identify the server's bottleneck.

For large layer publication or snapshot refresh, use
`LayerPublishing__MaterializationTimeoutSeconds`. Its default is 300 seconds;
valid values are integers from 1 through 3600. It controls the wall-clock and
PostgreSQL command budget for the canonical snapshot copy, including delete
work during refresh. It does not increase ordinary feature query timeouts or
ignore caller cancellation. Read [materialization settings](references/materialization.md).

Apply a justified value through the installation's supported configuration
path, preserving the prior value. In a governed installation, propose any
required configuration/restart and wait for a separate approver; do not edit
containers to evade policy. Changing `.env` alone has no effect unless Compose
actually forwards that setting to the serving service.

After the approved change, verify readiness, the operation's terminal state,
and a bounded query of the published layer. Compare duration and resource use
with the baseline. Roll back the setting through the same approved path if the
change does not help. Report the value, measured effect and remaining limit.
