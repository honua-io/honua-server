# Release Bundle (Compatibility Train)

The release-bundle orchestrator turns the manual release checklist into one
evidence-first CI run. It builds **one** Native AOT release-candidate image, runs
the integration/conformance and Esri-SDK evidence against that exact image
digest, cuts every SDK against the same candidate (dry-run), assembles the
release-train manifest, and runs the honua-devops validator to emit the
release-train scoreboard.

Foundation-first: it composes the building blocks that already exist and is
honest about coverage — suites not yet wired to the prebuilt image are reported
as *not-yet-gated*, never silently skipped. Widen coverage by editing the
registry (`release/bundle-suites.json`).

## Pieces

| File | Role |
| --- | --- |
| `.github/workflows/release-bundle.yml` | Orchestrator: build RC image → integration evidence → SDK cut → aggregate → gated promote |
| `release/bundle-suites.json` | Data-driven registry of integration suites, SDKs, and lanes (mode + how each pins to the RC image) |
| `scripts/release/dispatch-and-wait.sh` | Dispatch a workflow (cross-repo), wait, emit a one-line evidence JSON |
| `scripts/release/collect-evidence.sh` | Merge per-suite results + registry → the generator's `evidence.json` |
| `scripts/release/build-release-manifest.sh` | Assemble/refresh `release/<id>.json` from evidence (replaces hand-editing) |
| honua-devops `compat-train-release-validation.sh` | Consumes the manifest → release-train scoreboard bundle (verdict + owning follow-ups) |

The generator (`build-release-manifest.sh`) is the **producer**; the honua-devops
validator is the **consumer**. Together they replace the manual evidence
collection in [`RELEASE_CHECKLIST.md`](RELEASE_CHECKLIST.md).

## Run it

```
Actions → Release Bundle (Compatibility Train) → Run workflow
  release_id:  honua-2026-06-preview
  channel:     preview
  promote:     false   # evidence-first: build, test, generate manifest, validate — publish nothing
```

The run produces the `release-train-evidence` artifact: the refreshed
`release/<id>.json` manifest and the `release-validation-bundle.json` scoreboard
(per-surface pass/fail + the owning follow-up issue for every gap). Promotion
(`promote: true`) is a separate, protected-environment step that only proceeds
when the validator verdict is `pass`: it publishes the SDKs for real and retags
the immutable RC image to the channel + `latest-aot`.

### Required secret

`RELEASE_BUNDLE_TOKEN` — a PAT with `actions:write` on the SDK/esri/console/helm
repos (cross-repo dispatch; the repo-scoped `GITHUB_TOKEN` cannot trigger other
repos) and `contents:read` to check out honua-devops for the validator.

## Surfaces and the required-surface gate

The validator requires green/waived evidence on five surfaces: **server, sdk,
admin, helm, terraform**. Today server + sdk are wired; admin/helm/terraform are
`manual` lanes pending automation, so a foundation run validates to `fail` with
those three as the named gaps. That is the intended, honest state — flip each
lane in the registry as it gets real evidence.

## Widening coverage (the incremental work)

`refactorPending: true` entries in the registry need a `honua_image` /
`base_url` input added to their workflow so they consume the RC image instead of
building their own. Priority order:

1. SDK conformance (`sdk-dotnet`, `sdk-python`) — add a server-image input; this
   also closes the local-fallback gap (honua-sdk-python#50).
2. `client-interop-nightly` — accept a prebuilt image instead of rebuilding.
3. CITE suites — add an image input to `cite-conformance-common.yml`.
4. admin/helm/terraform lanes — emit RC-pinned evidence from those repos.

## Tooling tests

`scripts/release/smoke-build-release-manifest.sh` and
`scripts/release/smoke-release-bundle-pipeline.sh` exercise the deterministic
core (registry → evidence → manifest → validator round-trip) and run in CI via
`.github/workflows/release-bundle-tooling.yml`.
