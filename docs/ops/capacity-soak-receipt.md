---
type: reference
title: "Candidate capacity-soak receipt"
description: "Producing and verifying candidate-bound capacity observations and their attested evidence ZIP."
---
# Candidate capacity-soak receipt

The release gate consumes an attested **ZIP**, containing
`capacity-soak-receipt.json` (schema version 2) and the exact raw files it cites.
The approved producer is
[capacity-soak-candidate.yml](../../.github/workflows/capacity-soak-candidate.yml).
The normative envelope, frozen queries and thresholds live in honua-release
[`docs/CAPACITY-ENVELOPE-2026.1.md`](https://github.com/honua-io/honua-release/blob/trunk/docs/CAPACITY-ENVELOPE-2026.1.md).

## Dispatch and candidate identity

Create a branch or tag whose head is the manifest's exact server commit. That
commit must contain this producer. Dispatch the workflow **on that ref**, using
the matching immutable image and a committed release ref:

```bash
gh workflow run capacity-soak-candidate.yml -R honua-io/honua-server \
  --ref <candidate-ref> \
  -f candidate_sha=<manifest-server-sha> \
  -f candidate_image=ghcr.io/honua-io/honua-server@sha256:<manifest-image-digest> \
  -f lock_ref=<honua-release-commit>
```

The workflow refuses a producer source or checkout different from the candidate,
a source-built image, and a candidate/image pair different from the release
manifest. It resolves the release ref once and fetches the manifest, lock and
verifier from that same commit. The deployment's revision and Production posture
are read back from the running server. A later producer ref cannot qualify an
older candidate: GitHub attests the workflow's source commit.

`steady_state_seconds=0` uses the frozen minimum, currently 3,600 seconds.
`publish=false` still runs attestation and the full checker against the local ZIP.
A failing checker always fails the workflow, whether publication is enabled or not.

## Collection

The substrate is [local Docker](../../docker-compose.soak.yml): one server
replica, PostGIS, Redis and local file storage. All share one host failure domain.
The server boots its Production policy with a real per-run Pro license and a
throwaway password-protected key-ring certificate. Schema migrations run before
[fixture seeding](../../scripts/soak/seed_envelope.py); the served catalogue is
then republished. These observations carry no cloud or Preview capacity claim.

[collect_capacity.py](../../scripts/soak/collect_capacity.py) owns the eight
scenario populations in the soak profile: 30 feature, 15 spatial, 20 OGC feature,
10 CQL, 60 connection-pool, 5 large-result, 15 OData and 15 tile users. Each user
cycles independently. This collector replaces the legacy NBomber aggregate
receipt path; its population is measured directly, never reconstructed from
percentiles or summary counts. The workflow still checks the canonical profile's
concurrency before starting.

Before measurement, one existing layer-0 feature is updated through the authenticated
FeatureServer edit API to exactly the locked maximum canonical UTF-8 feature size.
The fixture advertises Update and gives its description field enough declared
capacity for that payload. A successful per-feature edit result and unchanged
geometry and attributes on read-back are required; no SQL write bypasses API
validation and no extra row is added. Samples re-read the
feature, catalogue, per-layer counts and tenant scope. The canonical feature
encoding is compact JSON with Unicode preserved, including geometry and all
returned attributes.

Each completed load, GP or probe request belongs to exactly one replica/container-incarnation
and one UTC interval of at most 30 seconds. The joint histogram retains count,
measured duration (rounded up to a millisecond), HTTP status, in-band error and
protocol. HTTP 200 error documents and malformed JSON remain failures. Transport
errors/timeouts are recorded as 599; they are never dropped. Requests completing
outside the measured window are excluded by the same rule for every outcome.

Metric and workload samples cover both endpoints and the complete window, with
no gap above 60 seconds. Samples retain their supporting observations. Worker
pressure is the server container's CPU consumption divided by the available host
CPUs (GP shares this process); database pressure is the server's measured pool
utilization; Redis pressure is connected clients divided by its `maxclients`.
The receipt gates the maximum of these three ratios. Queue depth and age are
observed through submitted GP jobs; configured queue limits are not substituted
for observed depths. Missing samples and background-task errors remain explicit
`samplingFailures` and cannot qualify.

Worker, database and Redis stop/start drills run **inside** the window. Each
retains injection, detected-stop and recovery timestamps plus its serving probe.
Database and Redis recovery also require direct dependency readiness. Deliberate
faults remain inside the load population and may cause the candidate to fail its
availability budget. The collector never excludes those failures to get green.

## Evidence and verification

[capacity_evidence.py](../../scripts/soak/capacity_evidence.py) computes the
receipt's values and populations from the raw observations. The one
`honua.capacity-observations/v1` artifact shares the receipt's candidate, window,
topology, producer and lock hash. It is uploaded first so the receipt can cite the
actual immutable Actions artifact URL. The ZIP is flat and bounded to 64 files,
64 MiB per file and 256 MiB total; it currently contains two files.

The workflow attests that complete ZIP with SLSA v1 on a GitHub-hosted runner and
publishes its bytes on the evidence branch at a commit-pinned HTTPS URL. It
fetches the published bytes, compares them, verifies the attestation with `gh`,
extracts with the release checker’s bounded extractor, and runs
`tools/check_capacity_soak.py`. The receipt's `signature` field points to the
external ZIP attestation; the field itself is not cryptographic proof.

The release checker independently recomputes all eight signals and verifies the
raw hashes, populations, eight workload dimensions, freeze, source commit,
workflow, run ID/attempt, image digest and ZIP subject digest. Green requires that
checker to accept everything. The legacy manual load workflow is diagnostic only;
its nightly schedule has been retired in favor of candidate qualification.

## Failed qualification

A failed or incomplete run is evidence of a failed or incomplete qualification.
Its ZIP may still be attested and published for diagnosis; the final check remains
red. Preserve it and investigate the failing dimensions or signals. Do not alter
thresholds, copy targets into observation rows, or relabel an incomplete window.

The pinned candidate at implementation time was
`87966c3f7b6c840ffc4d4da0b451714ab717b18a`, which predates this collector.
Qualification against this implementation requires a new manifest-pinned
candidate containing it. No run on a newer producer ref can repair that immutable
source mismatch. This PR's analytical and local HTTP fixtures are implementation
evidence, not a candidate soak or a cryptographic attestation.

The old GP driver reports that the single-worker admission limit rejects new jobs
while one runs; it does not fill the declared queue of 100. The new collector
records the actual depth. If a new candidate still behaves that way, its queue
workload fails the existing frozen contract and #5314 remains open until a real
full-envelope soak passes. Neither a passing ZIP fixture nor retiring the nightly
job waives that release requirement.
