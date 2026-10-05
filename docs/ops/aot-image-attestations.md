---
type: reference
title: "Lambda and Functions AOT image attestations"
description: "Immutable OCI serving identities and digest-bound SBOM and provenance for release locks."
---
# Lambda and Functions AOT image attestations

The 2026.1 platform lock promise is that a signed platform lock binds immutable
SBOM and provenance for every published component. This producer supplies the
container evidence for honua-release#231 WI-9; the release train must collect it
from the build of its own candidate under honua-release#376 R18–R21.

## Published identities

`nightly-container-build.yml` exports Lambda AOT separately for `linux/amd64`
and `linux/arm64`, and Functions AOT for `linux/amd64`. Each build exports a
single serving platform plus BuildKit attestations, using `provenance: mode=max`
and `sbom: true`, in the same OCI index structure as the server image. Functions
now exports a registry candidate rather than loading a local image, because the
classic Docker image store discards attestations.

On trunk, verified Lambda architecture indexes are copied without changing their
digests, then combined under the existing `nightly-lambda-aot` tags. Functions
publishes verified `nightly-functions-aot` and `nightly-functions-aot-<short-sha>`
tags. Tags are discovery pointers; the release pins
`oci://ghcr.io/honua-io/honua-server@sha256:<index-digest>`.
`deploy-platform-images.yml` also enables and verifies these attestations for
its published Lambda and Functions AOT candidates. It retains the Lambda parent
in each registry under `attested-lambda-aot-arm64-<full-source-sha>` and publishes
the verified arm64 serving child under its existing Lambda deployment tags.
Final Lambda aliases use `--prefer-index=false` to preserve the child digest.
Scan-only local images do
not supply immutable registry evidence.

An index digest and a serving image digest identify different bytes. Select a
runnable descriptor by `platform.os == linux` and the requested architecture;
exclude `vnd.docker.reference.type == attestation-manifest`. Its digest is the
per-architecture **serving child digest**. Lambda deployments and ECR mirrors
that require a single image manifest must use that child, retaining the parent
index as the immutable evidence root. `unknown/unknown` descriptors contain
attestations and must never be selected as a serving image. Attestations do not
add another runnable platform or change the child's layers.

## Verification and release consumption

With Docker Buildx and skopeo installed and authenticated to the registry:

```bash
image=ghcr.io/honua-io/honua-server@sha256:<index-digest>
docker buildx imagetools inspect "$image"
docker buildx imagetools inspect "$image" --raw
docker pull "$image"
python3 scripts/ci/verify-serving-image-boundary.py \
  --serving-image "$image" --require-attestations \
  --attestation-report attestations.json
```

The verifier checks the pushed index and serving child bytes against their
sha256 digests. For **every** serving child in that index, it finds attestation
manifest descriptors whose `vnd.docker.reference.digest` equals the child digest.
It checks the manifest digest, copies only its small evidence blobs, checks their
digests, and requires both SPDX (`https://spdx.dev/Document`) and SLSA provenance
(`https://slsa.dev/provenance/v0.2` or `https://slsa.dev/provenance/v1`)
in-toto statements. Each statement must name
the exact serving child digest in `subject[].digest.sha256`; its predicate must
match the layer annotation and contain data. Missing evidence, wrong subjects,
mutable references, corrupted bytes and registry read failures fail the job
before public tags move. Root filesystem verification also remains required.

The nightly artifacts `lambda-attestations-amd64`, `lambda-attestations-arm64`
and `functions-attestations-amd64` contain JSON discovery receipts with:

| Field | Immutable identity |
| --- | --- |
| `image` | Exported index, `oci://ghcr.io/honua-io/honua-server@sha256:<index>` |
| `platform` | Serving architecture, e.g. `linux/arm64` |
| `subject` | Serving child, `oci://ghcr.io/honua-io/honua-server@sha256:<child>` |
| `sbom.attestation`, `provenance.attestation` | OCI attestation manifest references, each pinned by digest |
| `sbom.digest`, `provenance.digest` | sha256 of the full corresponding in-toto statement blob |
| `*.predicateType` | Statement type used to select the layer |

Read the parent index by digest, then the referenced attestation manifest by
digest, then the selected layer blob. The layer digest identifies the bytes of
the **whole in-toto statement**, not a reserialized `predicate`. Both statements
may reside in one attestation manifest; their layer digests remain distinct.
For honua-release evidence rows, use `component` from the candidate's component
inventory, `uri` from `*.attestation`, and `sha256` from `*.digest`. The consumer
must interpret this OCI manifest URI by selecting the layer matching that sha256
and predicate type, rehash the fetched statement, and validate its subject against
the locked artifact's selected child. The current lock generator validates the
reference declarations; registry fetching and subject validation are additional
consumer responsibilities, not implied by successful draft generation.

The aggregate Lambda index may differ from either architecture index. Traverse
the locked aggregate directly and confirm it retains the same child and
attestation descriptors; an architecture receipt alone does not name that final
aggregate. Registry copies use `--all --preserve-digests` so evidence stays with
the image. GitHub keyless build provenance is also attached to the final Lambda
index and the Functions candidate, as it is to the canonical server index:

```bash
gh attestation verify "oci://$image" --repo honua-io/honua-server
```

BuildKit statements are content-addressed build evidence; GitHub attestations
provide the separate signed workflow identity. The signed platform lock binds
the selected image and evidence references. A retained workflow artifact or a
passing local check alone does not certify a release lock.

## Branch proof

Dispatch `nightly-container-build.yml` on the implementation branch. Branch runs
build and verify Lambda and Functions immutable candidates and upload the
receipts. They skip canonical/JIT builds and all public nightly tag promotion;
trunk runs retain those publication gates. Branch base-image mirrors use a
run-scoped `honua-server-base/proof-<run-id>` repository, preventing interference
with trunk base tags. Read `imagetools inspect` output in
the verification steps: each single-platform candidate must list its serving
manifest and an `unknown/unknown` attestation manifest with the exact child
reference annotation. This proves registry storage without moving the nightly
train's tags. A failed build or missing receipt is failed proof, not a waiver.

Storage format: [Docker attestation specification](https://docs.docker.com/build/metadata/attestations/attestation-storage/).
