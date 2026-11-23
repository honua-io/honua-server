---
{
  "name": "honua-tuning",
  "description": "Use when a Honua operator needs to diagnose slow layer publication and adjust documented materialization budgets without changing query or approval policy."
}
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
path, preserving the prior value. Size it from measured throughput (rows copied
before the budget ran out) with headroom, inside the documented range; if progress
is unavailable or the estimate exceeds that range, stop and report instead of
guessing or clamping. Where the installation offers a governed configuration or
restart proposal, use it and wait for human approval in Console. A Compose
installation has no such proposal for this setting: change only this key's line
in `.env` in place, record just the prior line as the backup (if the key is
absent, add one line and record the prior state as absent, the 300-second
default), never display or copy the whole file and keep its permissions; then
give the human the exact apply command (`docker compose up -d --no-deps honua` in
the install directory) and wait for them to confirm before you continue. Do not
recreate or edit containers yourself to evade policy. Changing `.env` alone has
no effect unless Compose actually forwards that setting to the serving service.

After the change is applied (an approved proposal or the human's apply command),
verify readiness, the operation's terminal state and a bounded query of the
published layer. Compare duration and resource use with the baseline. Roll back
the setting through the same path if the change does not help. Report the value,
measured effect and remaining limit.
