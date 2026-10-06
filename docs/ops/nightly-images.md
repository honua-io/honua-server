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
certified. A dispatched full matrix certifies like the scheduled one: when the
shard router resolves `run_all` (including an empty re-dispatch at an
already-verified head), `ci.yml` runs every lane, Build & Format Check,
.NET Foundation Tests and Python Integration Tests included. Pull requests still
skip by path.
