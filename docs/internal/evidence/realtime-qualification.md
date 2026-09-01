# Realtime candidate qualification

Realtime promotion is transport-specific and candidate-specific. The server consumes the
`honua.sdk.realtime-conformance-evidence.v1` receipt produced by the sdk-js transport-observe
lane; it never infers a pass from route availability.

Generate live evidence in the sdk-js checkout against the reviewed deployment, then project it:

```bash
node scripts/realtime-conformance-evidence.mjs --lane live \
  --output test-results/realtime-live-conformance-evidence.json --strict

python3 scripts/conformance/realtime/qualify_candidate.py \
  --evidence ../honua-sdk-js/test-results/realtime-live-conformance-evidence.json \
  --candidate-revision "$HONUA_CANDIDATE_REVISION" \
  --output test-results/realtime-qualification.json
```

Add `--require-qualified` only at the promotion gate. It exits nonzero until every SSE,
WebSocket, and OData cell has exact-candidate evidence for baseline completion, ordering,
duplicate behavior, resume/gap detection, reconnect under partition, and token expiry.

The receipt always includes explicit `not-yet-qualified` rows for HA failover, backpressure,
scale/proxy behavior, Redis failover snapshot recovery, sink outage recovery, tenant isolation,
and the 24–72 hour soak. Those rows may become qualified only from an explicit multi-node run;
absence is never treated as a skip or a pass.
