---
type: reference
title: "Custom-code worker runtime"
description: "Credential-free custom-code dispatch, attempt-scoped artifact transport, journal ingestion and bounded logs."
resource: "honua://capability/process.geoprocessing"
---
# Custom-code worker runtime

Custom Python and .NET jobs run in AWS Batch Fargate with a non-root UID of 1001,
no task IAM role, no injected secrets, no mounted volumes, and no cloud credential
environment. Before submission the server describes the exact job-definition ARN
and checks these properties and its image against the admitted image digest. The
provider execution role remains available to Fargate for image pulls and platform
logs; user code has no artifact-bucket credentials.

The server, worker images and `honua-iac` credential-free Fargate definitions must
be upgraded together. Legacy definitions with a job role fail admission at dispatch.
The server requires an AWS S3 or Azure Blob implementation of `ICloudFileStorage`
for new custom-code submissions. An existing keyed replay remains readable if that
configuration subsequently becomes unavailable.

## Artifact transport

The dispatcher prepares a fresh scoped token for each canonical attempt and
persists it with `HONUA_JOB_ID` and `HONUA_JOB_ATTEMPT` in its Provisioning CAS before
calling AWS. A recovered provider submission therefore uses the same durable token.
The configured `ControlPlane:AwsBatch:CustomCodeMaxQueueWaitSeconds` (default 3,600,
allowed 1–86,400) fixes a latest start time. Observers cancel jobs still queued or
starting after that deadline, and both harnesses refuse source fetch/build/execute
past it. The token expires after that start deadline plus the explicit execution
timeout and five minutes of upload grace, giving every admitted start its full
execution budget. Provider retries are disabled; canonical retries mint the next attempt's token.

The worker sends artifact bytes to:

```text
PUT /api/geoprocessing/jobs/{jobId}/custom-artifacts/{name}?attempt={attempt}
X-Api-Key: <scoped job token>
Content-Length: <exact bytes>
```

This capability is deliberately visible to user code. It authorizes the declared
Honua resource scope and this job's artifact transport, not general cloud storage.
The broker requires the running attempt and exact token persisted by the dispatcher.
It reserves an immutable server-selected object key, verifies length and SHA-256,
and attaches a receipt to the durable job. The worker retries temporary failures
and the short interval before the server observes the job as Running. Authenticated
owners can retrieve available artifacts through the same path with GET.

Limits are 32 artifacts, 64 MiB per output, 256 MiB aggregate, and 1 MiB for the
reserved `honua-events.v1.jsonl` journal. The broker's byte receipt is independent of
claims made by user code. The worker's sanitized child environment is defense in
depth; the credential-free Fargate definition and broker authorization provide the
credential boundary.

## Journals and retained logs

Both production harnesses initialize the broker before fetching or building user
code, so ordinary setup, tool and upload failures can publish their final journal.
The server checks its immutable receipt, overrides the claimed attempt with the
broker attempt, redacts and projects bounded events, and atomically deduplicates
retry ingestion by receipt digest and event position. Failure to publish the required journal changes an otherwise successful worker exit
to a harness failure; existing tool failures remain failures. Hard process termination or a
cancelled attempt can prevent a final journal upload; provider status remains the
terminal source of truth in that case.

The Redis execution-log store retains at most 2,000 entries of 4 KiB each per job.
Queries use absolute append cursors, so trimming earlier entries does not shift a
cursor onto different events. Tail and step responses disclose total and dropped
counts; diagnostics includes a truncation warning. Canonical terminal metadata
survives oversized messages. The legacy `GetLogsAsync` helper returns at most the
latest 500 entries; use explicit pagination when reading the retained history.

Source and CI tests exercise the definition guards, real HTTP upload transports,
Redis ingestion, crash recovery and bounded log reads. They do not constitute a
live cloud security or deployment qualification receipt.
