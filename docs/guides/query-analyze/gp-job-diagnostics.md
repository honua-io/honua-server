---
type: guide
title: "Diagnose a remote geoprocessing job"
description: "Read one job diagnostics projection to find the failed phase and attempt, judge whether the worker is alive, see what code ran, and decide whether a retry would help."
---
# Diagnose a remote geoprocessing job

When a job fails on a server you cannot attach a debugger to, the questions are
always the same: what exactly ran, how far did it get, is anything still running,
which step failed and with what, are the outputs usable, and will a retry help.
`GET /api/v1/admin/jobs/{jobId}/diagnostics` answers them in one versioned
document (`honua.job-diagnostics/v1`) built from the same durable job record,
execution log and artifact store the other admin job routes read. It adds
nothing the server does not hold: evidence that is missing or truncated is
listed under `evidence`, never filled in.

In an MCP client connected to Honua, call `honua_diagnose_job` with:

```json
{"jobId": "your-job-id", "events": 100}
```

Read `observed.status` for the job outcome and `evidence` for missing records.


`events` bounds the recent event tail (default 50, maximum 500). Responses are
`no-store`; the `X-Correlation-Id` header carries the job's correlation id.

## What each section answers

| Section | Question | Notes |
| --- | --- | --- |
| `identity` | Which job, backend, target, workload and queue? | `providerOperationId` and `workloadId` appear only in the operator view. |
| `release` | What code ran? | `state` is `complete`, `partial` or `unavailable`; `missing` names each absent field. Custom-code jobs carry runtime, repository, git ref, entry point and dependency manifest. Managed processes carry the server version plus the plan and process definitions. A historical job that recorded no provenance says `unavailable` instead of guessing. |
| `lifecycle` | How far did it get? | `phase` is canonical: `waiting-for-capacity`, `waiting-for-retry`, `starting-worker`, `fetching-code`, `executing`, `publishing-results`, `completed`, `failed`, `cancelled`. `timingSource` is `measured` only when the record carries a claim timestamp; otherwise the numbers are `inferred` from record updates. |
| `attempt` | Which attempt is this and would a retry help? | `retry.allowed` follows the same rules as `POST .../retry`; `wouldRepeat` states what a retry re-runs and whether earlier attempts left committed effects. |
| `worker` | Is the worker alive? | `liveness` is judged against the job's own heartbeat policy: `alive`, `stale` (past the interval), `lost` (past the timeout; the reconciler will fail or retry the attempt), `not-claimed`, `finished` or `unknown`. |
| `failure` | Which step failed and with what? | Classification `execution_failed`, `worker_lost`, `timeout` or `cancelled`; the failing step's phase and ordinal, structured `errorCode`, `nativeExitCode` and a sanitized `stderrExcerpt` when the worker recorded them; a `recovery` sentence. |
| `steps` | What ran, in order? | The same sanitized per-step view as `/steps`; durations are inferred from consecutive log timestamps. |
| `events` | What did the worker report recently? | The most recent `limit` events with `totalCount` and `truncated`; page `/logs` for the rest. |
| `artifacts` | Are the outputs usable? | `complete`, `partial`, `unavailable` or `none`, with counts of expired and unresolved references. |
| `evidence` | What is the projection not telling me? | `missing`, `truncated` and `notes`. |

Commands, paths, credentials and input metadata are sanitized before they leave
the server, exactly as on `/steps`: workspace paths become `<scratch>` or
`<path>`, credentials become `[REDACTED]`. Local glass-box output stays local.

## Views

Every principal that can read the job gets the projection. An execute-scoped
operator sees the `operator` view with worker and provider identities. A
read-only principal, such as a developer with an `admin:read` key, sees the
`owner` view: the same evidence with those infrastructure identities withheld,
and a note saying so.

## Reading a failure

1. Start with `failure.classification` and `failure.recovery`.
2. `worker_lost` and `timeout` are infrastructure outcomes: check worker
   capacity and the job's policies before changing the process.
3. `execution_failed` with a `nativeExitCode` or `stderrExcerpt` points at the
   step named in `failure.phase`; the matching `steps[stepOrdinal].command` is
   the sanitized command line.
4. Check `release.state` before reproducing locally. A `partial` or
   `unavailable` release means the exact code is not recorded; say so in the
   ticket instead of assuming trunk.
5. Check `artifacts.state` before promising outputs to a caller: `partial`
   means some references expired or could not be resolved.

## From an AI client

`honua_diagnose_job` returns the same projection for an MCP caller, restructured so the
agent cannot mistake the server's reading for the record: `observed` holds the recorded
facts, `inferred` holds the canonical phase, liveness verdict, failure classification,
recovery advice and retry disposition, `evidence` lists what is missing or truncated, and
`caveat` repeats that a returned diagnosis is not proof the job succeeded.
`honua_job_evidence` returns only steps, bounded recent events and artifact availability.
Both are read-only and use the same Job Read authorization as the REST route; an agent
with a read-only key gets the owner view.

The same projection backs the SDK, CLI and Console surfaces as they adopt it
(honua-release#613, stage 3). Local reproduction from this evidence is
tracked separately (honua-server#6220).
