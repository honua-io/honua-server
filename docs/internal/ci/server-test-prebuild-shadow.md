# Server-test build reuse history

The PR-gate build-reuse shadow was retired by operator ruling on 2026-08-31.
Its consumer instantiated only for `workflow_dispatch` runs over
`train/batch/*` refs. Since the train is now manual and release-candidate only,
the shadow could no longer gather representative evidence or reduce the
per-PR lander path.

The retired path included PR Gate payload packaging, trusted observation
receipts, merge-train source-run handoff, and a report-only Smart CI consumer.
Those components and the `HONUA_PR_GATE_BUILD_REUSE_SHADOW` repository variable
have been removed. A lander-side design may be revisited only after
step-breakdown data is available (#3343).

## Promoted shard prebuild consumer

The independently promoted attempt-1 shard prebuild consumer is unchanged.
`HONUA_SERVER_TEST_PREBUILD_CONSUME` and all of its trust, validation, fallback,
and rollback semantics remain governed by
[server-test-binary-artifacts.md](server-test-binary-artifacts.md).
