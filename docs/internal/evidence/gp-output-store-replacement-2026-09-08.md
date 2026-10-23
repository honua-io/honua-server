# Referenced GP output replacement proof

Issue: [#3900](https://github.com/honua-io/honua-server/issues/3900).
Release promise: whole-catalog GP GA must not report a successful referenced
output whose bytes disappear when the server or worker is replaced.

## Executed scope

The local Docker `output-store` lane passed **3/3 declared scenarios**, with zero
failed, missing or duplicate receipts: topology, output-store-attestation, cleanup.
The replacement scenario ran at 2026-09-08T09:52:01Z–09:52:38Z. It used the
repository's GP reliability Compose topology with isolated Postgres/Redis volumes
and host ports 23900/23901. This is pre-cut deployment evidence, not a candidate
or production backup-policy qualification.

The [complete unmodified summary](gp-output-store-replacement-2026-09-08.json.gz)
contains all three scenario receipts, observed image identities, container IDs,
state transitions, store attestations and the full host-normalized results
document. Its GitHub fields carry the harness's `local` defaults; this was not a
GitHub Actions execution.

| Identity | Value |
|---|---|
| Common server/worker source | `9f855c08365fd96d34ac5264eab9ab5a83a41ed3` |
| Server image | `ghcr.io/honua-io/honua-server@sha256:415d0a978c373f65dfd22f325b890f524f7e34cf0a11a7146038e475959171df` |
| Worker image | `honua-worker-3900@sha256:a9ab7d7e14abcdb6d1bdabd4f466892082228d260eeee01ae1daf64a59a2e483` |
| Harness implementation | `ef5e3a518` (subsequent test-only commit `9a76d408f` does not change the executed harness) |
| Store reference / class | `qualification` / `shared-persistent` |
| Backup identity | `gp-qualification-backup` |
| Store configuration digest | `bb6a13a6b7970d85b518145569fd6470eb6940524ced24fa0994fa1d391f4041` |
| Uncompressed receipt SHA-256 | `aa916501e8f917fd501b5f032368801ccdc50229d46bcaab7c34d7d32b664fbe` |
| Gzip archive SHA-256 | `754011e4d3649854eee39ae59baf6bbaf899edaace680d699ef08b31af591529` |

The server is the published successful nightly image. The worker was built
locally from the same source using the unmodified production
`docker/worker-gdal/Dockerfile`, its pinned native dependencies, and
`HONUA_GIT_SHA` set to that revision. Its digest identifies this local build;
the worker reference is not published in a remote registry.

## Assertions and independent expectation

- An existing unattested directory caused startup to exit unsuccessfully with an
  attestation diagnostic, without a timeout and without provisioning that directory.
- Server and replacement worker emitted the same credential-free store attestation.
- A real `gdal.ogr2ogr` job generated a 52,840-byte GeoJSON artifact, above the
  1,024-byte inline limit. A referenced object existed in the shared store.
- The normal authenticated content route returned `application/geo+json`.
  The independent oracle verified exactly 500 unique integer feature IDs 0–499,
  Point geometry and two finite WGS84 ordinates per feature. For ID `i`, expected
  longitude is `(-1578583 + i % 100) / 10000` and latitude is
  `(213069 + i % 50) / 10000`. Absolute ordinate tolerance is `1e-10` degrees.
  Raster nodata is inapplicable to this vector fixture.
- All server, peer and worker containers were destroyed and recreated. The
  replacement worker remained running and resolved the declared store digest.
  Reading from the replacement peer returned identical full bytes and media type;
  the host-normalized results document also remained identical.
- Before and after SHA-256 was
  `633dcf9227517dc22a218bc4cddeaf2f28ddb5f4c7f77eb03c4134cacdb85fc4`.
  This is observed byte-preservation evidence; the independent correctness oracle
  is the feature/grid expectation above, not a snapshot hash of GDAL output.
- Cleanup removed the isolated containers and database/Redis volumes.

The first deployment attempt reached the same assertions but failed to serialize
its 157 KiB evidence object through `jq --argjson`: Linux rejected the oversized
argument and left an empty receipt. That run is not counted as passing evidence.
The writer now reads files and publishes receipts atomically, and the summary
marks malformed or empty receipts missing. A 256 KiB executable regression failed
before the fix and passed after it; a serialization-failure injection also proves
that missing receipts remain non-passing. Seven focused Python tests passed,
including eight corruptions of the feature fixture.

## Acceptance disposition

The runtime attestation and topology binding remain delivered by PRs #4363 and
#4510. This receipt supplies the previously missing pre-cut deployment/replacement
execution criterion using the same attested configuration as #3852.

The release decision record still reports **Candidate digest: not yet cut**.
The exact-candidate crash-boundary proof under #3852 and the signed release/DR
receipt, including production topology and backup inventory, remain released until
those images exist. The existing forced-staging restore regression is not a signed
candidate DR receipt. Issue #3900 stays open for that qualification.
