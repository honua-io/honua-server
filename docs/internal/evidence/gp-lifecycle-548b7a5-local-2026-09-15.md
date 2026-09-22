# GP lifecycle lane on the pinned candidate 548b7a5 (local, not a pass)

This is an observation, not a qualification receipt. It is retained for #3809
and its #3849–#3857 cells. Neither #3900 nor #3949 depends on it: those issues
exclude the shared crash, retry, cancel and soak cells.

No GDAL worker image is published for the pinned source, so the
`GP Lifecycle Qualification` workflow cannot be dispatched for this candidate.
This run used the method from the release DR gate's `gp-outputs` job instead.
The unmodified `docker/worker-gdal/Dockerfile` at
`548b7a5263da5a3f2381eb43f232687cdf92b0bf` was built with the same registry
cache, and the result was pushed to a local registry
(`gp-candidate-worker@sha256:8df8f8c2e9fc42443b4ddd86d05c1aeaafd7c89b074dd7e1371feebfbdcc6179`).
`scripts/qualification/gp-lifecycle-harness.sh` from that source then ran with
`HONUA_GP_LANE=lifecycle` against
`ghcr.io/honua-io/honua-server@sha256:29974ee7b722e3ae15c3b891024e5e70800f412188aeccf5ec3d32d9dac675c1`,
on this host's Docker Desktop, under concurrent lane load.

`gp-lifecycle-548b7a5-local-2026-09-15-summary.json.gz` is the unmodified
summary: 30 declared scenarios, 30 receipts, none missing or duplicated,
**14 passed and 16 failed**.

| Outcome | Scenarios |
|---|---|
| pass | topology, output-store-attestation, sync, async, cancel-claimed, restart-server-{accepted,terminal,results-read}, restart-redis-{accepted,terminal,results-read}, restart-postgres-{terminal,results-read}, cleanup |
| fail | cancel-{native-process-started, output-bytes-written-unpublished, artifact-reference-published-terminal-cas-pending}, idempotency, retry, timeout-{cooperative,ignoring}, restart-worker-{accepted,running,terminal,results-read}, restart-server-running, restart-redis-running, restart-postgres-{accepted,running}, duplicate-delivery |

What the retained logs show:

- **Native fence never observed.** In each cancel and timeout cell the worker
  claims the job and logs `Job execution started`. The harness then never sees
  `native-process-started.ready.json` within its 60 s `wait_barrier` bound, so
  the scenario exits without a job-specific finding.
- **Idempotency.** The second submission with the same `Idempotency-Key`
  returns HTTP 409, and `auth_curl --fail-with-body` discards the body. The
  receipt records "second idempotent submission failed".
- **Retry.** The seed job reaches `dismissed`, the worker logs
  `Job abandoned … Reason=Worker shutdown` and `Job requeued`, and the manual
  retry is then recorded as "retried job was lost".
- **Harness contamination.** `run_timeout_live` exports
  `HONUA_GP_TIMEOUT_SECONDS=2`, but it restores `3600` only on its success path
  (`gp-lifecycle-harness.sh:1025`). When the fence wait returns early, every
  later cell recreates the worker with a 2 s workload timeout. The worker then
  logs `Job timed out: …, MaxDuration=00:00:02` in the restart-worker and
  duplicate-delivery cells. Their `duplicate terminal state or
  orphaned/changed output` findings therefore cannot be attributed to the
  runtime until the lane is rerun with that state isolated.

Nothing here separates a harness defect from a runtime defect for the first
three items. The lane has no hosted run to compare against: the dispatchable
workflow has zero runs.
