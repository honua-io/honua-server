# Layer resource qualification

The 2026.1 whole-catalog GP GA promise requires bounded execution as well as
correct results. `qualify_layer_bounds.py` runs the production OGC execution API,
PostGIS source reader and Redis job runtime in a disposable one-CPU, 1 GiB
deployment of an exact image digest. It publishes the fixture with the repository's
canonical metadata seed, then restarts the server before execution so the declared
configuration and catalog are read from durable state.

```bash
python3 tests/dotnet/Honua.Server.Tests/Features/Geoprocessing/Execution/qualify_layer_bounds.py \
  --image ghcr.io/honua-io/honua-server@sha256:<manifest-pinned digest> \
  --receipt /tmp/layer-resource-receipt.json
```

Port 18459 must be free, or supply `--port`. The runner retains timestamped server
logs next to the receipt and removes its containers and volumes even when
qualification fails. Python optimization flags are rejected because assertions are
the oracle.

The literal fixture has two area-4 squares overlapping by area 1, a second layer
with the same squares for joining, and one 1,001-vertex ring. The configured
`MaxTopologyWork=99` is below both the dissolve admission cost `(5+5)^2=100`
and the join cost `(5+5)*(5+5)=100`. Buffer intermediates also exceed that work
budget. A passthrough job must return exactly the two original polygon coordinate
arrays and the expected process/count metadata. The oversized ring and each
topology refusal must reach terminal failure with an actionable message after
exactly one execution attempt, read from the server's job log. Concurrent
FeatureServer count queries must keep returning exactly 2.

The runner then recreates the server from configuration with a deadline profile:
`MaxLayerExecutionSeconds=8`, and input and topology budgets that admit a spatial
join of two 6,000-feature layers of overlapping five-vertex diamonds. That join is
36 million exact predicate calls, well beyond the deadline on one CPU, so only the
elapsed-time bound and dismissal can stop it:

- `elapsed-time-limit`: the job fails naming `MaxLayerExecutionSeconds=8`, after one
  attempt that lasts at most the deadline plus 4 seconds.
- `dismiss-running-join`: the join is dismissed while it is computing (progress 55,
  container CPU at least 50%). The job reaches `dismissed` within 10 seconds, the
  container CPU drops below 50%, and three deadlines later the job is still
  dismissed with no second attempt.

## Receipts

`layer-resource-candidate-receipt.json` records the September 13 run of the earlier
harness on the manifest-pinned Native AOT web image from source
`7ba422672e0c751843b17beb36e954a019cc19fb`. Passthrough geometry and oversized-ring
rejection passed. Dissolve, join and buffer qualification failed: that image
predates #4740 and completed those jobs despite the declared topology ceiling.

`layer-resource-nightly-3d82e84-receipt.json` runs the current harness on the
published nightly Native AOT image
`ghcr.io/honua-io/honua-server@sha256:b1669510d574f92cd143fdab00fdf92d3b1e1fbaf873e173ad8f7ae7816b4a5c`
(source `3d82e8472703474957ab0624a339c4853952c96c`, which contains #4740). Every
topology, vertex and elapsed-time limit is enforced with the expected message, each
elapsed-time attempt stops at the 8-second deadline, and dismissal stops the running
join within 0.1 seconds. The receipt still fails: every deterministic refusal was
re-run three times through the retry policy, about 95 seconds of backoff before the
terminal outcome, and the elapsed-time job spent three deadlines of worker time on
the same input. All 2,259 serving probes passed, with no OOM kill.

`layer-resource-branch-diagnostic-receipt.json` runs the same harness on a
framework-dependent diagnostic image built from this branch's Debug output, pushed
to a local registry so it is addressed by digest. It is not a release candidate.
It passes every scenario: each refusal and the elapsed-time failure run once (8.5
seconds), dismissal is confirmed in 0.07 seconds and CPU falls from 99% to 30%,
and all 264 serving probes pass.

The runtime correction makes input, resource-limit and deadline refusals terminal
(`IsRetryable = false`) in the layer and enrichment executors; transient source-read
failures keep their retry budget. #4629 closes only once a manifest-pinned image
that contains this correction passes this harness. Managed topology is not
preemptible inside a call: admission limits its input work, and callers observe
cancellation between calls. These receipts do not claim a measured worst-case
runtime for every admitted geometry, an isolated worker kill guarantee, or the
shared crash/retry/storage qualification owned by #3848/#3852.
