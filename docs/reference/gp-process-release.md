---
type: reference
title: "GP process release descriptor and lock"
description: "The honua-gp.yaml project descriptor, the immutable process release lock it produces, how the lock digest is computed, and the honua-gp-devkit release command."
---
# GP process release descriptor and lock

A custom geoprocessing process is released as one immutable unit: the **release lock**
(`honua.gp-process-release-lock/v1`) names the exact source commit, dependency lock, runtime
image, workflow version, configuration references, execution targets, built artifact and
validation receipts that a deployment may activate. The lock is produced from a committed
**descriptor** (`honua-gp.yaml`, `honua.gp-process/v1`) by `honua-gp-devkit release`, and it is
the object that [honua-release's reference workflows](https://github.com/honua-io/honua-release/blob/trunk/docs/GP-PROCESS-RELEASES.md)
promote and deploy by digest. Nothing downstream rebuilds.

A release is identified by the digest of its lock's canonical JSON (see
[Lock digest](#lock-digest)). Two locks with the same digest are the same release; formatting of
the file never changes identity.

## Descriptor: `honua-gp.yaml`

Commit the descriptor in the process project, next to the code it describes. Paths are relative
to the descriptor's directory.

```yaml
schema: honua.gp-process/v1
process:
  name: summary-tool                 # ^[a-z][a-z0-9-]{1,62}$ — the logical process name
  version: 1.2.0                     # semantic version
  runtime: python                    # python | dotnet
  entrypoint: process.summary_tool:execute
  contract:
    inputSchema: schema/inputs.schema.json
    outputSchema: schema/outputs.schema.json
source:
  repository: honua-io/honua-samples # owner/name; --source-repository overrides
dependencyLock: requirements.lock    # or packages.lock.json for .NET
runtimeImage: ghcr.io/honua-io/honua-gp-worker@sha256:<64 hex>   # --runtime-image overrides
workflowVersion: wf-2026.1-7
configuration:                       # references only, never values
  - name: HONUA_DATA_BUCKET
    reference: ref:environment/data-bucket
targets:
  - kind: kubernetes-job             # kubernetes-job | aws-batch
    chartVersion: 2026.1.0
  - kind: aws-batch
    jobDefinitionName: honua-gp-worker
compatibility:
  serverContractVersion:
    minimum: 3
    maximum: 4
artifacts:
  include:                           # files or directories packed into the release artifact
    - process
    - schema
    - requirements.lock
validation:
  receipts:                          # candidate-bound JSON results produced after committing the source
    - name: golden-tests.json
      path: artifacts/golden-tests.json
```

| Field | Rules |
|---|---|
| `schema` | Must be `honua.gp-process/v1`. Unknown keys anywhere are an error. |
| `authoring` | Optional local devkit settings: template identity, exact `toolchainVersion`, explicit `harnessRoot` override, source/build/assembly paths, fixtures and expected artifacts. `init` writes this section and `test` reads it alongside `process.runtime` and `process.entrypoint`; it makes no remote validation claim. |
| `process.*` | Name, semantic version, runtime, entrypoint, and the two contract schema paths. The lock records the schemas' SHA-256 digests, not the files. |
| `source.repository` | `owner/name`. The commit SHA is never declared: it is read from the checkout (and must equal `--source-sha` when given). |
| `dependencyLock` | Path of the committed dependency lock; its digest goes into the lock. |
| `runtimeImage` | `registry/repository@sha256:<64 hex>`; a tag is not an identity. |
| `configuration[]` | `name` is an environment-variable style name; `reference` must start with `aws:secretsmanager:`, `azure:keyvault:` or `ref:`. A value is refused. |
| `targets[]` | At least one. `workerImageDigest` defaults to the runtime image digest; `chartVersion` (Kubernetes) and `jobDefinitionName` (AWS Batch) are optional. |
| `compatibility.serverContractVersion` | The inclusive serving↔worker execution-contract window the process supports. |
| `artifacts.include` | Relative paths inside the project. Empty means the whole project directory minus `.git`, `.venv`, `__pycache__`, `node_modules`, `bin`, `obj`, `artifacts` and similar tooling folders. |
| `validation.receipts[]` | At least one. Each file must satisfy `honua.gp-validation/v1` below. The lock hashes its bytes and records `status: pass` only after result and candidate validation. The devkit does not execute these tests. |

### Validation receipt

Commit source inputs first, run the tests against that commit and runtime image, then
write a receipt under an ignored `artifacts/` directory outside `artifacts.include`:

```json
{
  "schemaVersion": "honua.gp-validation/v1",
  "status": "pass",
  "sourceSha": "<full Git HEAD SHA>",
  "runtimeImageDigest": "sha256:<exact worker image digest>",
  "tests": { "passed": 4, "failed": 0, "skipped": 0 }
}
```

The source SHA and runtime image digest must match the release inputs exactly. Every
target worker image digest must equal this validated runtime digest; an untested target
image override is rejected. At least
one test must pass, with no failed or skipped tests. Malformed JSON, duplicate fields,
unsupported schemas, arbitrary text/JUnit and receipts over 64 KiB are rejected.
Receipts are hashed evidence, not source files: do not commit a receipt into the source
revision it purports to test or include it in the source artifact. A changed receipt
changes the release lock digest. The release workflow must independently establish its
producer's identity and attest the actual run; a self-authored JSON document is not an
authenticated CI attestation, even when its declared results and bindings validate.

## Lock: `honua.gp-process-release-lock/v1`

`honua-gp-devkit release` writes `<output>/process-release-lock.json` and
`<output>/artifacts/<process>-<version>.tar.gz`. The lock's fields are exactly those of the
JSON Schema honua-release publishes as `schemas/gp-process-release-lock.v1.schema.json`:

| Field | Source |
|---|---|
| `process.{name,version,runtime,entrypoint}` | descriptor |
| `process.contract.{inputSchemaSha256,outputSchemaSha256}` | SHA-256 of the schema files |
| `source.{repository,sha,descriptorPath}` | descriptor / Git HEAD / path relative to the repository root |
| `dependencyLock.{path,sha256}` | repository-relative path and SHA-256 of the dependency lock |
| `runtimeImage.{reference,digest}` | descriptor or `--runtime-image` |
| `workflowVersion` | descriptor |
| `configurationReferences[]` | descriptor, sorted by name |
| `targets[]` | descriptor, worker digest defaulted |
| `compatibility.serverContractVersion` | descriptor |
| `artifacts[]` | the built tarball's name, SHA-256 and size |
| `validation.receipts[]` | SHA-256 of each receipt file, `status: pass` |
| `generatedAt` | the source commit's committer time in UTC (`--generated-at` overrides) |

Every value is declared, read from the checkout, or passed explicitly; the devkit never invents
one. The server reads and writes this document through `Honua.Geoprocessing.Release.GpProcessReleaseLock`.

### Determinism

Re-running `release` for the same descriptor at the same commit produces byte-identical lock and
artifact files, so a re-run is a no-op with the same digest:

- `generatedAt` is the commit's committer time, not the wall clock.
- The artifact is a `tar.gz` with entries in ordinal path order, ustar format, uid/gid 0,
  mode `0644`, and `generatedAt` as every modification time. Nothing from the build machine
  (user names, timestamps, directory order) reaches the bytes.
- A checkout with uncommitted changes under the release inputs is refused; a release is built
  from a commit, never from loose files.

### Lock digest

```
digest = "sha256:" + SHA-256( canonical JSON )
canonical JSON = keys sorted ordinally at every level, no whitespace, UTF-8 without ASCII escaping
```

This is byte-identical to Python's `json.dumps(lock, sort_keys=True, separators=(",", ":"),
ensure_ascii=False)` used by honua-release's `tools/gp_process_release.py lock-digest`, and the
committed lock `release/gp-process-locks/<process>/<first 12 hex>.json` is named by it. The
server implementation is tested against that repository's committed lock.

## Command

```bash
honua-gp-devkit release --descriptor honua-gp.yaml --output out/release \
  --source-repository honua-io/honua-samples --source-sha "$(git rev-parse HEAD)"
honua-gp-devkit release verify out/release/process-release-lock.json --artifacts out/release/artifacts
```

| Option | Meaning |
|---|---|
| `--descriptor, -d <path>` | Descriptor path (default `honua-gp.yaml`). |
| `--output, -o <dir>` | Required. Receives `process-release-lock.json` and `artifacts/`. |
| `--source-repository <owner/name>` | Overrides `source.repository`. |
| `--source-sha <sha>` | Must be the checked-out commit; refused otherwise. |
| `--runtime-image <ref@sha256:…>` | Overrides `runtimeImage`. |
| `--generated-at <yyyy-MM-ddTHH:mm:ssZ>` | Overrides the commit time. |
| `--json` | Machine-readable result (digest, paths). |
| `verify <lock> [--artifacts <dir>]` | Validates a lock, prints its digest, and checks the artifact bytes. |

Exit codes: `0` success, `1` the release cannot be built or verified (every finding is listed),
`2` usage error.

## Binding and admission

Publishing the lock to the server, the per-process binding (versioned, compare-and-set activation, rollback to the previous release) and admission-time pinning of AWS Batch job-definition revisions and Kubernetes worker images are described in the [GP process release bindings runbook](../operator/geoprocessing-process-releases.md).
