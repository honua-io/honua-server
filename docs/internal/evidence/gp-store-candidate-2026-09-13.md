# Manifest-pinned GP persistent-store qualification

The 2026-09-12 operator ruling requires qualification before the candidate cut.
This execution uses the image actually pinned by honua-release trunk, recorded
in the accompanying manifest snapshot. Release PR #342 was still draft when
that pin was resolved; its proposed 9f2f16a image was not substituted.

| Identity | Observed value |
|---|---|
| Release manifest commit | `f6c54b4396bdadb76676be7b839de71fb9a3de84` |
| Manifest blob | `5bcb44cd4117e64b232f881790ae3954cfce7f12` |
| Server source | `7ba422672e0c751843b17beb36e954a019cc19fb` |
| Server image | `ghcr.io/honua-io/honua-server@sha256:dd50cd81c057e37e73a6144572abdfc90d48de314d7625c54c4ef3b6eb65b0fd` |
| Local production worker | `honua-worker-3900@sha256:2db944efd297749f5765b7aaa6e89ddade95bb1281fd0cd2b81e63bc99dcf00a` |
| Store reference / persistence class | `qualification` / `shared-persistent` |
| Backup identity | `gp-qualification-backup` |
| Configuration digest | `bb6a13a6b7970d85b518145569fd6470eb6940524ced24fa0994fa1d391f4041` |

The published server image's GitHub provenance was verified against the nightly
container workflow and `refs/heads/trunk`. The compressed image-attestation file
retains that verification result. It authenticates the image; it does not sign
these locally executed GP receipts. The worker was built from the identical
source using the unchanged production `docker/worker-gdal/Dockerfile`; its image
digest identifies that local build, not a separately published worker release.

## Executed restore proof

The complete, unmodified `gp-store-candidate-2026-09-13-dr.json.gz` summary
records **4/4 passing scenarios**, zero failures, zero missing receipts and zero
duplicates. Its uncompressed SHA-256 is
`6bbc8e653581ad2a07d2155f4ac577c3d003a9b8b436570be6a4e55c92048aba`.

A real `gdal.ogr2ogr` operation produces 500 deterministic Point features and
52,840 bytes of GeoJSON, exceeding the 1,024-byte inline threshold. The independent
oracle checks every unique feature ID and coordinate. The normal authenticated
server content route returns SHA-256
`633dcf9227517dc22a218bc4cddeaf2f28ddb5f4c7f77eb03c4134cacdb85fc4` before and after
replacement and disaster recovery; the normalized descriptor also remains equal.

The drill stops both serving hosts and the native worker, takes cold backups of
PostgreSQL, Redis and the declared GP volume, destroys the original named volumes
and output contents, and verifies empty destinations before restoring. Every
restored file inventory matches its backup inventory. Fresh serving and worker
containers resolve the same attestation and the peer server reads the recovered
artifact through the ordinary result/content routes.

## Crash qualification finding

The final full matrix (`crash-attempt-4.json.gz`, uncompressed SHA-256
`53a99f0d00fa41d41c8001c8d4a4b284f23c50017bd3e1b5b474f47b9f27d6f2`)
is **7/8 passing, 1 failing**, including topology and cleanup. Five crash cells
passed. Both SIGKILL recoveries before terminal commit converged naturally; the
terminal-commit SIGKILL cell also recovered result registration on normal read.

The terminal-commit/store-outage cell failed: job
`gp-8eec9733145e480291e94936afff6825` returned 503 from its artifact content route
while `/healthz/ready` still returned 200 with the volume marker absent. No
assertion was relaxed to accept that response. A separate fresh topology then
removed only the marker and received **Ready/200 on all six consecutive probes**
one second apart. The retained readiness-probe log records that reproduction.

The candidate therefore cannot supply a complete passing signed qualification.
This is a reproduced behavior of the manifest-pinned image, not a dependency on
cutting a candidate. A corrected published image must pass the same checks and
become the manifest pin before the combined proof can close #3900. The precise
runtime cause of the readiness discrepancy has not been established here.

## Retained unsuccessful attempts

The earlier summaries are retained unchanged and never counted as green proof:

- `startup-failure`: the old PostgreSQL health probe admitted the temporary
  Unix-socket-only initdb server. The fixture now waits for TCP readiness.
- `first-restore`: cold restore passed, but the separate replacement job failed
  with `Execution-job reconciliation failed due to InvalidOperationException.`
  Its Redis backup retains that terminal record. The later complete run passed
  both scenarios with no runtime code change.
- `crash-attempt-1`: the production worker's UID owned the fence directories,
  preventing host-side release. The controller now explicitly grants access to
  those qualification directories. The old cleanup also left the optional
  proxy running; it was removed and cleanup now includes that profile.
- `crash-attempt-2`: the first SIGKILL recovery passed; moving a worker-owned
  output directory failed. Store disruption now uses a root helper inside the
  isolated worker container and restores the fixture even after assertion failure.
- `crash-attempt-3`: the replacement worker started just before Redis restarted
  and exited with a Redis startup connection failure. The run was interrupted,
  cleaned up and recorded as failing. Recovery now waits for backing services
  before starting the replacement worker.

## Receipt consumption

The `output-store-dr` and `crash-boundaries` lanes share the same attested Compose
configuration. The six crash cells are the shared exact-image boundary artifacts
for #3852; its retention, hold and abandoned-object remainder is not qualified
here. `gp-candidate-binding.py` rejects mismatched pins, missing cells and partial
restore before a receipt can be consumed by the release DR gate.

The companion honua-release change embeds GP proof in the existing full-platform
DR envelope before signing, attesting and publishing it to honua-evidence. It
reruns on manifest repins and makes `gate-dr` verify the embedded proof against
its own frozen manifest. Local compressed evidence alone is not a claim that
this trusted signing/publication step has executed.
