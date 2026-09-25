# Candidate GP canary evidence

`gp-buffer-canary.yml` runs at 00:17, 06:17, 12:17 and 18:17 UTC. Its
`harness-checks` job uses mocks only. A green harness check, manual dispatch, or
retry is not a production burn-in interval or whole-catalog GP qualification.
Issues #3809 and #3857 remain open until their live evidence requirements hold.

PRs also run `published-image-rehearsal` against the digest-pinned server image
in the workflow. It reuses the reliability Compose PostGIS/Redis/server services
and checks the pulled image's source label. The exact same canary input, async
HTTP execution, result decoding and numerical oracles run against that server.
This catches response-shape/default-format assumptions that mock tests cannot.
The ephemeral Development/API-key/loopback HTTP topology provisions a runner-local
output directory through the existing store initializer, using the resolved Compose
contract and checking its digest. The managed buffer/dissolve executors publish
inline artifacts; their OGC value-transmission results remain inline even with
staging enabled. The rehearsal retains both raw result documents and decoded
output checksums. It does not prove native staged-reference retrieval, storage
durability/backups, production bearer authentication, native-worker identity or
persistent output recovery; those remain open under #3809. Its separate receipt schema records
`qualification: false` and can never enter the scheduled streak. No cloud
deployment or production data is touched by that rehearsal.

## Frozen deployment configuration

The operator configures these repository values before the first interval:

| Setting | Required value |
| --- | --- |
| `GP_CANARY_URL` variable | Credential-free HTTPS base URL of the candidate deployment. |
| `GP_CANARY_TOKEN` secret | Bearer token authorized to read the capability manifest and execute/read the two GP jobs. |
| `GP_CANARY_RELEASE_REF` variable | Full 40-character commit in `honua-io/honua-release` containing the frozen `platform-manifest.yaml`. Mutable branch names are rejected. |
| `RELEASE_BUNDLE_TOKEN` secret | Existing cross-repository release workflow credential, with `contents:read` access to private `honua-io/honua-release`. No write permission is needed by this canary. |

The harness downloads that exact manifest through the authenticated GitHub
contents API and retains it. The repository-scoped `GITHUB_TOKEN` cannot read
the private release repository; missing/inaccessible release credentials produce
an explicit failure receipt. The separate `GITHUB_TOKEN` still reads this
repository's scheduled runs and artifacts. The harness resolves the
server source SHA and image digest using the existing GP candidate-binding
validator. Before and after executing the jobs, it reads
`/api/v1/capabilities/manifest` and requires `server.deploymentRevisionSource`
to be `image-digest` and `server.deploymentRevision` to equal the frozen server
digest. The deployment must supply its actual pushed digest through the existing
`HONUA_IMAGE_DIGEST` configuration. A source-only revision, missing identity,
mutable version, or two matching workflow variables cannot establish this proof.
This canary does not independently attest a native worker image or replace the
separate candidate-bound lifecycle/resilience and whole-catalog qualification.

## Frozen numerical workloads

Both jobs use the advertised OGC processes execution endpoint with
`Prefer: respond-async`, preserving their operation IDs, polling transitions,
terminal status, latency, input/output JSON and SHA-256 checksums.

- `geometry.buffer`: EPSG:3857 point (1000, 2000), radius 100. One valid polygon,
  envelope [900, 1900, 1100, 2100], expected 32-segment area
  `16 * 10000 * sin(pi / 16)` and symmetric difference against an independently
  constructed regular polygon. Input CRS, process and distance metadata must agree.
- `geometry.dissolve`: 4,096 overlapping 2-by-2 squares, four groups of 1,024.
  Each group covers its analytically specified 33-by-33 rectangle, area 1,089,
  with group origins at X = 0, 100, 200, 300. Four valid polygons, exact groups,
  input count, output count and EPSG:3857 metadata are required. Every area,
  envelope and symmetric-difference error must be at most `1e-6` in projected
  units (squared units for area). Expected shapes do not come from replaying a
  dissolve against the inputs. This is a managed geometry workload, not a claim
  that a tiny fixture qualifies a native processing load.

Both use fixture version `projected-buffer-dissolve-4096.v1`. Each job has a
180-second polling deadline and bounded HTTP responses. Missing configuration,
HTTP errors, unavailable artifacts and oracle failures produce failure receipts.
The second operation still runs when the first fails, provided preflight identity
was established. Credentials are never included in the evidence.

## Scheduled continuity and retained proof

The streak builder reads authoritative GitHub run metadata and downloads the
named artifact from each prior scheduled run. It rechecks both retained input
fixtures, output checksums, numerical metrics and candidate identity. A workflow
summary alone cannot count. Original scheduled attempts only qualify; skipped,
cancelled, failed, retried, receiptless, stale and artifact-expired runs break the
streak. Duplicate or missing six-hour slots also break it. The maximum accepted
scheduler start delay is 90 minutes; each receipt must complete within two hours
of its scheduled slot. These limits do not backfill missed intervals.

Seven qualifying intervals span at least 36 hours from first slot to last slot.
All must share the exact endpoint, release-manifest hash/ref, server image/source,
fixture version and numerical-oracle source hash. Each harness commit is retained;
an unrelated trunk commit alone does not restart the streak. Changing the
candidate, fixture or oracle does. Manual runs always report `ready: false`.

Each interval artifact is named with run ID and attempt and retained for 90 days.
The current interval is uploaded before streak evaluation, then downloaded and
verified through the same path as historical evidence. An absent current upload
cannot establish readiness. Passing
historical intervals are copied into `intervals/<run-id>/` with their receipts
and all input/output files. A separate immutable `.tar.gz` and SHA-256 file retain
the complete bundle, including the seven-slot streak report. Readiness is only
published in this separate bundle after interval verification. This provides the
reviewable proof; publishing a release
asset and signing/aggregating release evidence remain release-operator work.
