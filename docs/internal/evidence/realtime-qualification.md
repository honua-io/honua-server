# Realtime candidate qualification

Realtime Preview qualification is transport-, surface-, execution-, and candidate-specific.
The required `Realtime Preview Qualification` workflow consumes the immutable SDK artifact;
local copies and route availability never qualify a row.

The SDK artifact must be produced by `.github/workflows/realtime-live-conformance.yml`, be named
`realtime-cross-transport-conformance-<run-id>`, and contain `realtime-preview-evidence.json` in
`honua.realtime-preview-evidence.v2` format. It binds the server commit and image digest, SDK
commit and package, candidate environment, workflow/run/attempt/artifact, generation window,
and an executed assertion receipt for every exact row. Dispatch the server workflow with those
same immutable identities. The workflow verifies the source run and artifact before download,
projects with `--require-qualified`, and retains the source, diagnostics, ledger, and verdict for
180 days.

For local contract testing, use the same complete identity set:

```bash
python3 scripts/conformance/realtime/qualify_candidate.py \
  --evidence realtime-preview-evidence.json \
  --candidate-revision "$HONUA_CANDIDATE_REVISION" \
  --candidate-image "$HONUA_CANDIDATE_IMAGE_DIGEST" \
  --candidate-environment "$HONUA_CANDIDATE_ENVIRONMENT" \
  --sdk-package "$HONUA_SDK_PACKAGE" --sdk-revision "$HONUA_SDK_REVISION" \
  --workflow-repository honua-io/honua-sdk-js \
  --workflow-name 'Realtime Preview Qualification' \
  --run-id "$SDK_RUN_ID" --run-attempt "$SDK_RUN_ATTEMPT" \
  --artifact-id "$SDK_ARTIFACT_ID" \
  --source-artifact-url "$SDK_ARTIFACT_URL" \
  --qualification-run-url "$QUALIFICATION_RUN_URL" \
  --output test-results/realtime-preview-ledger.json --require-qualified
```

The 2026.1 Preview denominator covers feature-stream baseline completion, resume/gap detection,
partition recovery, token expiry, and tenant isolation for SSE, WebSocket, and OData; OData
lossless state convergence; and separate SensorThings SSE/WebSocket loss recovery, token expiry,
and tenant isolation. Missing, failed, skipped, stale, replayed, fixture, aliased, or identity-
unbound rows reject the ledger with cell-specific reasons.

Ordering/duplicate depth, HA and proxy routing, Redis failover, broker outage recovery,
saturation/backpressure, and the 24–72 hour soak are 2026.2 operational-graduation rows. They
remain valuable evidence but are deliberately not required to ship realtime as Preview in
2026.1 and cannot be submitted as aliases for a Preview-floor row.
