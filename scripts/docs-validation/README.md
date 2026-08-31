# Executable documentation validation

`validate-quickstart.sh` proves the primary install quickstart against a clean,
isolated Docker Compose project. It extracts commands directly from
`docs/get-started/quickstart.md`; there is no second command sequence in the
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
The harness always removes its containers and volumes on exit.

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
