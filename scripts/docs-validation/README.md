# Executable documentation validation

## Public installation guide

The canonical versions for the published installation journey live in
[`install-artifacts.json`](../../docs/get-started/install-artifacts.json).
Update that manifest and the corresponding examples when publishing new
artifacts. No version lives in a C# test. Check their consistency locally:

```bash
python3 scripts/docs-validation/install_artifacts.py
```

`PR Gate` runs that lightweight check, validates the derived agent indexes on
the docs-only path, and the docs link gate checks links, examples and semantics.
The docs-only router recognizes the installation
manifest and derived agent indexes, while retaining runtime checks for any
document or data file actually consumed by server code/builds/tests.

Only changes to the artifact manifest, executable fences in the quickstart or
Linux/Windows install procedures, or the proof harness select the public Docker
proof. Wording-only edits do not. Run the same proof manually with:

```bash
python3 scripts/docs-validation/validate_public_install.py
```

It requires a local Linux Docker daemon, Compose, Python 3.11+ with working venv
and pip, and OpenSSL. It anonymously pulls the advertised image, executes the
annotated public quickstart, checks the published sample's identities and
coordinates, and verifies readback after restart. It creates a unique Compose
project with unused loopback ports, tears down only its own resources, and
writes a secret-free receipt to `artifacts/docs-validation/public-install/`.
There is no server compilation. The Linux and Windows procedure fences trigger
this shared-image proof when they change; it does not claim native Windows or
full Linux-package-procedure execution. Windows readback checks still run in
the docs gate, and native Windows replay has its own script.

The manual **Docs Public Install** workflow runs the same proof independently.
When changing the advertised image, refresh the observed preview-artifact
receipt from a successful run; that receipt is evidence, not generated prose.

## Source-development quickstart

`validate-quickstart.sh` proves the source-development quickstart against a clean,
isolated Docker Compose project. It extracts commands directly from
`docs/internal/developer/source-quickstart.md`; there is no second command sequence in the
harness.

Each `bash` or `sh` fence in the quickstart must have an immediately preceding,
stable annotation:

```text
<!-- docs-validation:quickstart.example mode=run -->
```

Use `mode=skip reason=...` only for commands that cannot be automated as part of
the local proof, such as cloning the checkout, an optional external image, or a
deliberately long-running interactive server. The extractor rejects missing or
duplicate annotations and skips without a reason.

Run the full proof from the repository root:

```bash
bash scripts/docs-validation/validate-quickstart.sh
```

Failure evidence is retained under `artifacts/docs-validation/quickstart/`.
By default, the harness removes its containers and volumes on exit.

Set `HONUA_DOCS_PRESERVE_STACK=1` to leave the validated Compose project and
its volumes running for a subsequent local journey stage. The caller then owns
teardown; for the default project, run
`docker compose --project-name honua-docs-quickstart down --volumes --remove-orphans`.

## Path to required at RC

Wave 1 runs nightly and by manual dispatch as an advisory, non-required lane.
Promote it to the 2026.1 RC gate after all of the following are true:

1. Thirty consecutive scheduled runs are green, excluding documented GitHub
   runner or registry outages.
2. The lane has a named owner and a five-business-day response SLO for drift.
3. Failure artifacts contain the annotated block list, Compose status/logs, and
   the sampled MVT needed to diagnose every asserted outcome.
4. The release workflow invokes this exact script for an RC candidate image or
   immutable source revision, and `honua-release` records the resulting check as
   required evidence for the validated-docs pillar.

At promotion, keep the nightly schedule as the early-warning signal and add the
same script to the RC certification workflow as a required job. Do not make it a
required per-PR check: the proof intentionally performs a full clean image build.
