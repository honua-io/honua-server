---
type: reference
title: "AOT OCI attestation verification receipts"
description: "Registry proof for the Lambda and Functions AOT attestation producer."
---
# AOT OCI attestation verification receipts

The consumer procedure is [AOT image attestations](aot-image-attestations.md).
These receipts test producer behavior; they do not certify a release lock.

## Canonical server format compatibility

On 2026-10-03, the verifier's `verify_image_attestations` function read the
existing canonical server index below using Ubuntu 24.04's skopeo 1.13.3.
It fetched and rehashed both architecture manifests, their attestation manifests
and every SPDX/SLSA v1 statement, and verified both statements' child subjects.
This confirms compatibility with the published server's evidence format.

Index: `oci://ghcr.io/honua-io/honua-server@sha256:536e759ec615302c3ad3893c3784ecb732c29e3feadb72e1ecf3d0db3c825c15`.

| Platform | Serving child digest | Attestation manifest digest |
| --- | --- | --- |
| linux/amd64 | `sha256:7843864be202f85f979619652ba9a34b3616eaacd3627e0a8b0052c8880b31a6` | `sha256:939a5c2e2364ee4baed7162ceae98fdeb6af0b969cdb5c8461f38e1f2a3f3600` |
| linux/arm64 | `sha256:8eba5276e093319ce3c03a9ec4fc812f6096b2a1d5fde17a312e6d61711a36c6` | `sha256:de8cd71b8452031d7fccfb23af1da95dee1e040410d1eb5975a829616eedf528` |

## Focused producer checks

On 2026-10-03, the changed architecture project passed all 11
`ServingImageBoundaryTests` (zero failed, zero skipped), including the Lambda
serving-child publication and branch-mirror isolation guard:

```bash
dotnet test tests/dotnet/Honua.Architecture.Tests/Honua.Architecture.Tests.csproj \
  --configuration Release --no-restore \
  --filter FullyQualifiedName~ServingImageBoundaryTests
```

Project-scoped formatting with `--include` for `ServingImageBoundaryTests.cs`
and a `timeout 20m` wrapper passed. The registry subject/digest rejection
fixtures, plain serving-child promotion fixtures, both workflows' actionlint,
base-image mirror-map consistency, and documentation checks also passed.
