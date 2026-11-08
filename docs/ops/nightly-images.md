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

## Data-store image variants

The default serving images contain the PostGIS store. Import and export formats remain
available, including GeoPackage, Shapefile, GeoTIFF and Parquet. SQLite supports file I/O;
it is not an additional server data store. The runtime is chiseled-extra, with ICU,
rendering fonts and required native libraries, and no shell or package manager.

Use the JIT full variant for additional stores: `nightly-jit-full`,
`nightly-jit-<candidate-sha7>-full`, or a release's `-ecs-jit-full` tag. Pin the selected
image by digest. Full includes DuckDB, SQL Server, MySQL, Oracle, Redshift, Snowflake,
Databricks and the REST read-through backend. Oracle and Snowflake require the JIT
runtime; the full variant preserves that existing constraint. Both variants use the
same source revision and workflow. The full variant retains the JIT image's existing
support classification.

Configuring an additional primary store, secondary store section or warehouse feature
gate on the default image refuses startup and names the full variant. Explicitly disabled
store sections can remain in shared configuration. This does not change the default
PostGIS connection settings or enable experimental stores in the full variant.

Custom image builds use `HONUA_STORE_PROFILE=postgis` (default) or `full`. This axis
is independent of `HONUA_BUILD_PROFILE` and `HONUA_PROTOCOL_PROFILE`. Local `dotnet`
builds and existing provider tests retain the full store profile; an MSBuild publish can
select `-p:HonuaStoreProfile=postgis` explicitly.

The shell-free image provides `/app/honua-healthcheck`; use `--ready` for readiness
or omit it for liveness. `HONUA_HEALTH_PORT` selects the loopback port (default 8080).
The image's Docker health check probes readiness. Deployment probes must use direct execution. Pull-only Compose stacks inherit the
image health check, so pinned images retain their own probe. Older images lack the
new executable; retain their existing probe when pinning a pre-change image, and update the image
and probe together. The ECS module exposes `health_check_command` and
`health_check_port` for that transition.

Connection creation, updates, metadata validation and retained-revision activation reject stores absent from the image.
If an existing database contains such a Connection, startup refuses with the same full-image
instruction. Recover with the matching full image; remove an unused Connection through the
normal administration workflow before returning to the PostGIS-only image.

Authenticated callers can read `dataStores` from `/api/v1/capabilities/manifest`.
`profile` is `postgis` or `full`; `installedProviders` lists canonical provider identifiers
compiled into that image, including stores whose experimental gates are disabled.
`fullImageTagSuffix` is `-full` on the PostGIS profile and null on the full profile;
`documentationHref` links to this guide. Anonymous
responses omit `dataStores`. Clients must use this inventory, rather than infer support
from a tag; an absent inventory does not establish support for any additional store.

The Store Image Size workflow measures both JIT variants and the default AOT image.
Its artifacts contain before/after byte counts, publish file manifests, startup-refusal
logs, format smoke evidence and scan results. It verifies that default publish output
has no excluded store assemblies or native libraries, retains file I/O libraries, and
contains no satellite resource assemblies.

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
