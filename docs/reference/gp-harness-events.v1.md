---
type: reference
title: "GP harness events (honua.gp.events/v1)"
description: "The structured event journal every custom-code harness run, local runner run, and devkit run writes: record kinds, bounds, truncation disclosure, and how the job step view reads it."
resource: "honua://capability/process.geoprocessing"
---
# GP harness events (`honua.gp.events/v1`)

Custom-code tools used to report progress only as free text on stderr, so a remote
job showed inferred timings and could not tell a progress line from a warning.
Every harness run now also writes a bounded JSON Lines journal with one record per
event. The stderr text is unchanged; the journal is a second, machine-readable
channel beside it.

The same shape is written by:

| Producer | Journal location |
| --- | --- |
| Python custom-code harness (Batch container) | `CUSTOMCODE_EVENTS_PATH`, default `/work/out/honua-events.v1.jsonl`; uploaded through the job-scoped artifact broker as the reserved artifact `honua-events.v1.jsonl` after the tool's own artifacts |
| .NET custom-code harness (Batch container) | same variable and upload behaviour |
| `honua-gp-python` / `honua-customcode-harness local` (local runners) | `<output>/<runId>/events.jsonl`, referenced by `receipt.json` (`eventsSchema`) |
| `honua-gp-devkit run` | `<receipt-dir>/<runId>/events.jsonl` next to `receipt.json` (default `.honua/runs`, `--receipt-dir`, `--no-receipt`) |

## Record

Every line is one JSON object. Fields present on every record:

| Field | Type | Meaning |
| --- | --- | --- |
| `v` | integer | Schema version, always `1`. |
| `seq` | integer | Journal sequence, contiguous from `0`. |
| `at` | string | UTC wall-clock time, RFC 3339 with millisecond precision and a `Z` suffix. |
| `elapsedMs` | integer | Milliseconds on the producer's monotonic clock since the run started. Durations derived from this are **measured**; durations derived from `at` or from job-log timestamps are **inferred**. |
| `attempt` | string | Attempt identity: `HONUA_ATTEMPT_ID`, else `AWS_BATCH_JOB_ATTEMPT`, else `1`; `local` for the local runners and `devkit` for the devkit. |
| `kind` | string | One of the kinds below. |

Kinds and their extra fields:

| `kind` | Extra fields | Written when |
| --- | --- | --- |
| `phase` | `phase` | The harness enters a step: `inputs`, `source`, `dependencies` (Python) or `build` (.NET), `client`, `execute`, `upload`; `setup`, `execute`, `artifacts` for local runs. |
| `progress` | `percent` (0–100), `phase` | The tool calls `context.progress.report(pct, phase)` / `IProgressReporter.Report`. |
| `log` | `level` (`info` or `warning`), `message` | The tool or harness logs a line. Messages are capped at 2000 characters, phases at 500. |
| `artifact` | `artifact: { name, sizeBytes, sha256?, uri? }` | An artifact is published (local: with its digest; remote: with its upload URI). |
| `failure` | `error: { type, message }`, `phase?` | The tool raised, returned a failed `GpResult`, or the harness failed a step. |
| `truncated` | `dropped`, `maxEvents`, `maxBytes` | Written once, immediately before `terminal`, when any record was dropped by a cap. |
| `terminal` | `status` (`succeeded`, `failed`, `setupFailed`, `cancelled`), `exitCode`, `phase?` | Always the last record. `phase` names the step the run stopped in when it did not succeed. |

## Bounds

A journal holds at most 1000 records and 256 KiB. Once either cap is reached, further
non-terminal records are counted and dropped; the `truncated` record discloses the
count and the `terminal` record is still written. The local receipts repeat the facts as
`eventsTruncated` and `eventsDropped`. Nothing is dropped silently.

## Readers

A reader treats any line that is not a v1 record (malformed JSON, another `v`, an
unknown `kind`) as a text line, never as an event, so a tool that writes junk into the
channel degrades to text instead of breaking inspection.

The server forwards a journal into the job's execution log through
`HarnessEvents.ToExecutionLogEntries` (`src/Honua.Geoprocessing`): one log entry per
record, messages passed through the same credential and path redaction as stderr,
metadata `honua.event.v`, `honua.event.kind`, `honua.event.seq`, `honua.event.attempt`,
`honua.event.elapsedMs`, and `honua.event.percent`, under the job-event cap (1000 entries,
256 KiB). Overflow becomes one warning entry carrying `honua.event.dropped`; the
`terminal` record is forwarded regardless.

The console job step view (`GET /admin/jobs/{id}/steps`) reports `timingSource` per step:
`measured` when both the step and the next one carry `honua.event.elapsedMs`, otherwise
`inferred` from log timestamps. Treat inferred durations as approximate.

## Reading a local receipt

```bash
honua-gp-python --source ./my-tool --entrypoint main:execute --input features=input.geojson
# {"runId":"…","status":"failed","receipt":".honua/runs/…/receipt.json","failedPhase":"execute","error":{…}}
jq -c 'select(.kind=="failure" or .kind=="terminal")' .honua/runs/*/events.jsonl
```

`receipt.json` records `status`, `failedPhase`, `artifacts` with hashes, `events`
(the journal file name), `eventsSchema`, `eventsTruncated`, and `eventsDropped`. Local
runs are unsanitized development evidence, not support bundles.
