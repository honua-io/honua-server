# Admin Authorization capacity evidence — 2026-10-12

The full matrix at `0fd0f53b91c2042810a42b7215afcd5f6d1abeaa`
([run 38193561888](https://github.com/honua-io/honua-server/actions/runs/38193561888))
terminated Admin Authorization after 1,322 seconds at its 22-minute inner limit,
with output three seconds before termination. Exit 124 and the absent final TRX
are retained as a capacity failure, not a passing result or an idle hang.

The prior complete observation at `01449eaed054a508e1fae691c768c26f0f2f19fb`
([run 38182837651](https://github.com/honua-io/honua-server/actions/runs/38182837651))
passed all 146 cases in 956 seconds. Per-case start/end intervals identify a
whole-class split without weakening assertions or dropping method-name matches
that the broad original filter selects in other classes.

| Proposed owner | Baseline cases | Baseline interval union |
|---|---:|---:|
| Admin Authorization Matrix | 67 | 355.510 seconds |
| Remaining Admin Authorization | 79 | 623.787 seconds |

The new matrix owns every case in `AdminAuthorizationTests` and
`RedisAdminApiKeyEndpointsTests`. Their intervals overlap, so summing class spans
would overstate the work. These are estimates from one complete observation,
excluding fixed setup overhead; they are neither a p90 nor fresh passing proof.
Both children retain the 22-minute test and 32-minute job limits, original
project and source triggers. The new matrix also receives Redis image prefetch.

`test-admin-authorization-partition.py` compares actual runnable source methods
with the frozen original filter and rejects missing, duplicate and foreign
methods, changed source triggers and changed budgets. The canonical router runs
this check alongside its complete partition and ownership checks. Source-method
counts are distinct from expanded, tier-filtered test-case counts.

The [JSON receipt](admin-authorization-capacity-20261012.json) preserves run,
source and artifact identities, raw hashes, the complete baseline counters and
the censored failure timing. Two actual fresh executions of each changed child
and the normal PR gates remain required before merge. Full-server and release
image qualification remain separate.
