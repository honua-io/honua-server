# Installed MCP durable-job cancellation

`cancel-durable-job.mjs` is a server-owned adapter for the existing GP lifecycle
harness. It uses the published official MCP SDK 1.32.1 installed by `npm ci`
from the reviewed canonical producer lock at
`honua-io/geospatial-mcp@372d9ad1366a5fa28165e7ebbc7481e6e1ca13c4`.
The workflow authenticates that immutable checkout and lock hash. The adapter
checks the installed version and lock hash and retains the package integrity,
lock hash and its own source hash. There is no workspace SDK implementation or
floating package fallback.

The lifecycle matrix preserves its 30 existing scenarios and adds
`mcp-cancel-native-process-started`. It submits a real native job through the
first server, observes its live worker child PID at the existing qualification
barrier, then uses the SDK against the peer replica to discover and invoke
`honua_cancel_job` twice for that exact durable job. The API key stays in the
process environment and request header; it is not an argument or receipt field.
Each call must return the same typed `cancellation_requested` acknowledgement.

That acknowledgement explicitly declares `executionCessationProven: false`.
The enclosing harness must still observe Dismissed, reject any observed success,
observe the native child exit, reject readable results immediately and after a
late-result interval, and find zero retained output objects. An acknowledgement
cannot bypass those checks. Request-level `notifications/cancelled` and local
client abort are separate mechanisms and do not cancel this durable job.
Existing duplicate-delivery, timeout, restart and output-boundary cases remain
in the matrix; this addition does not independently qualify those cases.

The existing **GP Lifecycle Qualification** workflow runs adapter wire controls
and shell negative controls on changes to these files. Scripted peers and shell
doubles are labelled controls and cannot qualify a Honua candidate. The real
matrix still runs only through its existing manual dispatch with approved
`server_image`, `worker_image` and `source_sha` inputs. Both images must be
immutable digests representing that source; the harness independently observes
the running identities. Do not substitute a locally built worker for a missing
published candidate. A current attested server/worker pair and retained passing
matrix are still required for qualification.

The installed adapter is restricted to the owned loopback Compose endpoints.
It has a 20-second request budget and a five-second per-tool-call timeout, rejects
redirects and malformed/contradictory acknowledgements, and closes its SDK
session on all exits. Its diagnostic output does not echo server error bodies.
The workflow retains separate control-count/source evidence with
`candidateQualified: false`; lifecycle receipts retain the real scenario outcome.
