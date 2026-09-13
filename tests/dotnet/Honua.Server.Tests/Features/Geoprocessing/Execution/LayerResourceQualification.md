# Layer resource qualification

The 2026.1 whole-catalog GP GA promise requires bounded execution as well as
correct results. `qualify_layer_bounds.py` runs the production OGC execution API,
PostGIS source reader and Redis job runtime in a disposable one-CPU, 1 GiB
deployment of an exact image digest. It publishes the fixture with the repository's
canonical metadata seed, then restarts the server before execution so the declared
configuration and catalog are read from durable state.

```bash
python3 tests/dotnet/Honua.Server.Tests/Features/Geoprocessing/Execution/qualify_layer_bounds.py \
  --image ghcr.io/honua-io/honua-server@sha256:dd50cd81c057e37e73a6144572abdfc90d48de314d7625c54c4ef3b6eb65b0fd \
  --receipt /tmp/layer-resource-receipt.json
```

Port 18459 must be free, or supply `--port`. The runner retains server logs next
to the receipt and removes its containers and volumes even when qualification
fails. Python optimization flags are rejected because assertions are the oracle.

The literal fixture has two area-4 squares overlapping by area 1, a second layer
with the same squares for joining, and one 1,001-vertex ring. The configured
`MaxTopologyWork=99` is below both the dissolve admission cost `(5+5)^2=100`
and the join cost `(5+5)*(5+5)=100`. Buffer intermediates also exceed that work
budget. A passthrough job must return exactly the two original polygon coordinate
arrays and the expected process/count metadata. The oversized ring must reach
terminal failure with an actionable vertex-limit message; normal retry/backoff
is retained. Concurrent FeatureServer count queries must keep returning exactly 2.

`layer-resource-candidate-receipt.json` records the September 13 run on the
manifest-pinned Native AOT web image from source
`7ba422672e0c751843b17beb36e954a019cc19fb`. Passthrough geometry and oversized-ring
rejection passed. **Dissolve, join and buffer qualification failed:** the candidate
completed those jobs despite the declared topology ceiling. All 544 serving probes
passed, with maximum observed latency 1.662 seconds; the container was not OOM-killed,
and teardown completed. Those availability observations do not turn the failed
resource-admission checks into a passing qualification.

The runtime correction adds cumulative vertex and topology admission, cancellation
deadlines between managed calls, and byte-limited streaming serialization. Focused
tests independently assert union area 7, join counts, threshold boundaries, XYZ,
Unicode/null/Int64 preservation, no later-row access after an oversized attribute,
and disposal after a cancelled source read. The matrix's catalog source digest is
refreshed for these executor changes; existing semantic evidence rows stay intact.

The pinned image does not contain this correction. #4629 remains open until a
replacement manifest-pinned image passes resource qualification, including the
declared deployment's cancellation/elapsed-time bounds. Managed topology is not
preemptible inside a call: admission limits its input work, and callers observe
cancellation between calls. This receipt does not claim a measured worst-case
runtime for every admitted geometry, an isolated worker kill guarantee, or the
shared crash/retry/storage qualification owned by #3848/#3852.
