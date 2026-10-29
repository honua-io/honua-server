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


`events` bounds each response (default 50, maximum 500). Omit `eventCursor` for
the recent event tail. To read earlier evidence through `honua_job_evidence`, use:

```json
{"jobId": "your-job-id", "events": 100, "eventCursor": "start"}
```

Pass the returned `events.nextCursor` as `eventCursor` until it is absent or null.
`events.firstCursor` starts again at the beginning of the retained stream. Pages
follow append order; they are not a frozen snapshot, so a running job may append
more events between reads. Retention can remove old evidence. Every page checks
job authorization again and uses the same redaction as the default view.

The HTTP diagnostics route accepts the same `events` and `eventCursor` parameters.
Responses are `no-store`; `X-Correlation-Id` carries the job's correlation id.

## What each section answers

| Section | Question | Notes |
| --- | --- | --- |
| `identity` | Which job, backend, target, workload and queue? | `providerOperationId` and `workloadId` appear only in the operator view. |
| `release` | What code ran? | `state` is `complete`, `partial` or `unavailable`; `missing` names each absent field. Custom-code jobs carry runtime, repository, git ref, entry point and dependency manifest. Managed processes carry the server version plus the plan and process definitions. A historical job that recorded no provenance says `unavailable` instead of guessing. |
| `lifecycle` | How far did it get? | `phase` is canonical: `waiting-for-capacity`, `waiting-for-retry`, `starting-worker`, `fetching-code`, `executing`, `publishing-results`, `completed`, `failed`, `cancelled`. `timingSource` is `measured` only when the record carries a claim timestamp; otherwise the numbers are `inferred` from record updates. |
| `attempt` | Which attempt is this and would a retry help? | `retry.allowed` follows the same rules as `POST .../retry`; `wouldRepeat` states what a retry re-runs and whether earlier attempts left committed effects. |
| `worker` | Is the worker alive? | `liveness` is judged against the job's own heartbeat policy: `alive`, `stale` (past the interval), `lost` (past the timeout; the reconciler will fail or retry the attempt), `not-claimed`, `finished` or `unknown`. |
| `failure` | Which step failed and with what? | Classification `execution_failed`, `worker_lost`, `timeout` or `cancelled`; the failing step's phase and ordinal, structured `errorCode`, `nativeExitCode` and a sanitized `stderrExcerpt` when the worker recorded them; a `recovery` sentence. |
| `steps` | What ran, in order? | At most 500 recent event-derived steps; omitted earlier steps are disclosed in `evidence.truncated`. `timingSource` distinguishes measured harness durations from inferred timings. Match a failure to a step by its `ordinal`, which is not necessarily the array index. |
| `events` | What did the worker report recently? | The most recent `limit` events by default, or a cursor page. `mode`, `firstCursor`, `nextCursor`, `totalCount` and `truncated` describe the view. |
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
   step named in `failure.phase`; find the step whose `ordinal` equals `failure.stepOrdinal` for its sanitized
   command line. Failure detail is bounded to the latest 500 retained events;
   inspect earlier event pages when truncation is disclosed.
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
(honua-release#613, stage 3). A bundle that carries GP evidence can be run
locally; see [From job id to local reproduction](#from-job-id-to-local-reproduction).

## From job id to local reproduction

`honua-gp-devkit reproduce` turns one exported diagnostic bundle into a separate
local run. It does not replay production writes, does not reuse production
credentials, connection strings or output paths, and does not check out another
commit or move HEAD. It refuses to run when an input or artifact is missing or
its bytes do not match the recorded hash, and it says which one.

Read the job with `honua_diagnose_job` (above) and keep the diagnostic bundle
that was exported with `gpEvidence`. Input bytes are present only when that
export was explicitly authorized. From the checkout that should match a recorded
source SHA:

```bash
honua-gp-devkit reproduce ./job-diagnostic-bundle.json
```

When the evidence records a release lock digest, pass the lock file you already
have. The command compares that digest, and the source SHA when the evidence has
one, to this checkout (`git rev-parse HEAD`) and to the lock. A mismatch names
the identity and stops.

```bash
honua-gp-devkit reproduce ./job-diagnostic-bundle.json --lock ./process-release-lock.json
```

Exported inputs are written into a new temporary scratch directory and passed to
the same local executors as `honua-gp-devkit run`. That directory, and the output
directory next to it, are deleted when the command finishes. A bundle written
before GP evidence still loads, and reproduce exits non-zero because the case is
incomplete. A reproduced failure is still a failure of the process: the command
exits 0 only when the local outcome matches the recorded one, including a
recorded native failure, and it says so. The devkit help text spells the same
verb as `honua gp reproduce`.
