# Trusted native-tools reuse

The PR and full-CI point-cloud proofs resolve the production
`docker/worker-gdal/Dockerfile` `native-tools` target through
`scripts/ci/native-tools-image.py`. They always execute their existing real PDAL
proofs afterward. Host-installed GDAL tests, managed worker publication, and AOT
serving-image gates are unchanged.

The resolver fingerprints the **entire** Dockerfile, Docker ignore files, reachable
local COPY inputs, global argument defaults and explicit overrides, target,
platform, pinned reachable base images, and publisher/resolver policy files.
Local COPY sources must be literal files or directories; literal directories
include hidden files and subdirectories. Wildcard and escape syntax is rejected
before normalization rather than approximating Docker matching with Python glob.
Unresolved or unsupported input syntax disables reuse and builds the production
target locally. The fingerprint excludes unrelated application source when that
source is not consumed by the native-tools target. Native source versions and
the PDAL archive checksum remain the production Dockerfile's pinned values.

The dedicated image namespace is
`ghcr.io/honua-io/honua-server-native-tools`; its registry build cache is
`ghcr.io/honua-io/honua-server-native-tools-cache:linux-amd64`. The publisher can
read the existing worker cache but never writes or evicts that worker namespace.
Consumers write neither shared image nor cache.

A content-keyed tag is only a lookup hint. Reuse requires all of:

- resolution to an immutable registry digest and a pull by that digest;
- matching input identity and Linux/amd64 image configuration;
- a GitHub artifact attestation for that exact digest, issued by
  `https://token.actions.githubusercontent.com`, for the canonical repository's
  `.github/workflows/native-tools-publish.yml` at `refs/heads/trunk`;
- the attested source commit matching the image's publisher revision label.

`gh attestation verify` checks the certificate identity, source ref and digest,
and rejects self-hosted publisher attestations. OCI-attached provenance supplies
verification material; no PR-authored manifest or supplied image digest is
trusted. Receipts record the selected registry digest, local image ID, input
fingerprint and publisher source SHA. Test containers use the immutable local ID.

The publisher has no PR trigger, checks both canonical repository and trunk ref,
and holds publication permissions only in that guarded job. It runs on relevant
trunk input changes, a weekly refresh, or a normal trunk workflow dispatch.
Weekly native-tools refreshes retain the production worker's native-tools
no-cache policy. Repository package visibility/access must allow intended
consumers to read the image; unavailable access safely falls back.

There is no invented bootstrap digest: until the first trusted trunk publication
and attestation complete after merge, every consumer builds as before. New
context input paths outside the publisher's trigger list remain safe misses
until the next scheduled/manual publication; add such paths to the trigger list
when extending the Dockerfile. Mutable package repositories and the upstream
installer can change rebuilt bytes despite identical declared inputs; the
receipt's attested digest identifies the actual trusted build, not a claim that
rebuilding later produces byte-identical layers.

Lookup, pull and provenance verification share a 120-second budget. Any missing
image, offline registry, failed verification, input/platform mismatch or rollback
uses the existing production target build, capped at 30 minutes. Owned command
groups are stopped before fallback begins. An unquiescent group fails the step
instead of starting overlapping work. The workflow allows five additional
minutes for bounded lookup and cleanup. A build failure remains a proof failure;
it never skips tests or claims successful reuse.

Set the single switch `HONUA_NATIVE_TOOLS_REUSE=false` (repository variable for
hosted workflows, environment variable locally) to force the old build path.
Default is enabled with fallback. No serving-image or AOT rollback is involved.

For local Linux proof runs, use the same resolver before the unchanged PDAL test
command. Existing Docker/GHCR and GitHub CLI authentication supplies read access;
credentials are never embedded in receipts:

```bash
python3 scripts/ci/native-tools-image.py resolve --output /tmp/honua-native-tools.json
export HONUA_PDAL_PROOF_IMAGE="$(python3 -c 'import json; print(json.load(open("/tmp/honua-native-tools.json"))["image_id"])')"
export HONUA_GDAL_PROOF_USER="$(id -u):$(id -g)"
```

This preparation does not replace the local supervisor's resource admission or
run tests itself. The coordinator must still admit the real proof execution.
The resolver also accepts an explicit `--platform linux/arm64`; that platform
currently builds locally rather than reusing the amd64 publication.
