---
type: reference
title: "Nightly images follow certification"
description: "Which trunk sha the nightly container build images, and why it is the newest certified one rather than trunk HEAD."
---
# Nightly images follow certification

The nightly image follows certification, not the clock. honua-release's nightly
resolver (honua-release#376, R18) pins a server candidate only when the trunk sha
is both imaged (`nightly-<sha7>`, `nightly-aot-<sha7>` and
`nightly-lambda-aot-<sha7>-<arch>`) and certified: the newest completed
`schedule` or `workflow_dispatch` run of `ci.yml` on that exact sha concluded
`success` and every certification lane in it succeeded. `ci.yml` declares those
lanes once, as the job ids in its workflow-level `CERTIFICATION_LANE_JOBS`, and
`nightly-container-build.yml` reads that list through
`scripts/ci/select-certified-nightly-sha.py`. On trunk, the nightly images the
newest of the 100 latest trunk commits that is certified, never HEAD at cron
time. It builds and checks out that sha, stamps `HONUA_GIT_SHA` and the
`org.opencontainers.image.revision` label with it, and publishes the per-sha tags
above plus the dated and channel tags (`nightly`, `nightly-<yyyymmdd>`, `trunk`,
and the `-aot`, `-lambda-aot`, `-jit` and `-functions-aot` variants), so those
channel tags now point at the newest certified image. The run log and step
summary name the chosen sha and its certifying run. When no commit in that
window qualifies, the run fails and lists the missing or unsuccessful lanes for
each of the newest candidates. Nothing is published. To image one specific sha, dispatch the
workflow on trunk with `candidate_sha=<full sha>`. That sha must itself be
certified.

## One sha per image

Image contents, `HONUA_GIT_SHA`, the `org.opencontainers.image.revision`
label and annotation, and the build provenance all name one sha, and that sha
is the run's own commit. GitHub (`actions/attest-build-provenance`, read by
`gh attestation verify` as `sourceRepositoryDigest`) and BuildKit can only
attest the commit the run was started at. A trunk run that checked out an
older certified sha would publish an image whose labels and provenance
disagree, and no release gate can pin it (run 38072453832, honua-release#520).

So when the selected sha is not trunk head, the trunk run builds nothing. It
creates the lightweight tag `nightly-candidate-<sha7>` at the selected sha
(or accepts an existing tag only if it already names that sha), and it
dispatches `nightly-container-build.yml` again at that tag, unless a run on
that tag is already queued or running. The tag run checks that the tag names
its own commit and that the commit is still a certified trunk sha. It never
dispatches again, and it builds, attests and publishes the per-sha, dated and
channel tags the way a trunk build does. Its attestations carry
`sourceRepositoryRef refs/tags/nightly-candidate-<sha7>` instead of
`refs/heads/trunk`. When trunk head is itself the selected sha, the trunk run
builds in place as before. Every build and attest job also fails fast when the
candidate is not `github.sha`. To check an image:

```bash
gh attestation verify "oci://ghcr.io/honua-io/honua-server@<digest>" \
  --repo honua-io/honua-server --format json \
  | jq -r '.[].verificationResult.signature.certificate.sourceRepositoryDigest'
docker buildx imagetools inspect "ghcr.io/honua-io/honua-server:nightly-<sha7>" \
  --format '{{json .Image}}' | jq -r '.. | .Labels? // empty | ."org.opencontainers.image.revision"'
```

Both commands must print the same sha. A dispatched full matrix certifies like the scheduled one: when the
shard router resolves `run_all` (including an empty re-dispatch at an
already-verified head), `ci.yml` runs every lane, Build & Format Check,
.NET Foundation Tests and Python Integration Tests included. Pull requests still
skip by path.
