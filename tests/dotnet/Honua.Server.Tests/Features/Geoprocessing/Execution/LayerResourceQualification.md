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
the oracle. Run it on a host without other heavy work: serving probes and job
submission keep a 10-second bound.

## Limit profile

The literal fixture has two area-4 squares overlapping by area 1, a second layer
with the same squares for joining, and one 1,001-vertex ring. The configured
`MaxTopologyWork=99` is below both the dissolve admission cost `(5+5)^2=100`
and the join cost `(5+5)*(5+5)=100`. Buffer intermediates also exceed that work
budget. A passthrough job must return exactly the two original polygon coordinate
arrays and the expected process/count metadata. The oversized ring and each
topology refusal must reach terminal failure with an actionable message after
exactly one execution attempt, read from the server's job log. Concurrent
FeatureServer count queries must keep returning exactly 2.

## Deadline and dismissal profiles

The runner then recreates the server from configuration with budgets that admit a
spatial join of two 6,000-feature layers of overlapping five-vertex diamonds: 36
million exact predicate calls, well beyond any deadline below on one CPU, and
30,000 x 30,000 vertices inside `MaxTopologyWork`.

- `elapsed-time-limit` (`MaxLayerExecutionSeconds=8`): the job fails naming the
  setting, after one attempt that lasts at most the deadline plus 4 seconds.
- `dismiss-running-join` (recreated again with `MaxLayerExecutionSeconds=300`, so
  only dismissal can stop the join): the join is dismissed while it is computing,
  with container CPU at 50% or more and the job still `running`. From just before the
  `DELETE` request, the job must reach `dismissed` and container CPU must fall below
  50% within 10 seconds. The job is then observed for 45 seconds after dismissal,
  past the default policy's 30-second first retry, and must still be dismissed with
  one attempt.

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
topology, vertex and elapsed-time limit is enforced with the expected message.
Elapsed-time attempts stop at 7.96 to 8.01 seconds, and dismissal stops the running
join in 0.07 seconds (CPU 101% to 9%). The receipt still fails because every
deterministic refusal ran three attempts through the retry policy. Each refusal
took 92 to 101 seconds from first attempt to terminal outcome, and the
elapsed-time job spent three deadlines of worker time over 124 seconds. All 2,495
serving probes passed, and there was no OOM kill.

`layer-resource-branch-diagnostic-receipt.json` runs the same harness on a
framework-dependent diagnostic image built from this branch's Debug output, pushed
to a local registry so it is addressed by digest. It is not a release candidate.
It passes every scenario:

- Each refusal is terminal after one attempt (0.08 to 0.24 seconds).
- The elapsed-time failure runs once, for 8.03 seconds.
- Dismissal, including the request, completes in 0.12 seconds, and CPU falls from
  102% to 10%.
- All 357 serving probes pass (maximum 2.93 seconds).

`layer-resource-candidate-548b7a5-receipt.json` runs the current harness on the
2026.1 candidate pinned on 2026-09-15,
`ghcr.io/honua-io/honua-server@sha256:29974ee7b722e3ae15c3b891024e5e70800f412188aeccf5ec3d32d9dac675c1`
(`org.opencontainers.image.revision` = `548b7a5263da5a3f2381eb43f232687cdf92b0bf`).
It fails, and it fails for exactly one reason: that commit is an ancestor of
#4881's merge (`927af8fbc`), so the pinned candidate does not contain the terminal-refusal
correction. Bounded passthrough and dismissal pass; every deterministic refusal is
enforced with the expected message but still runs three attempts:

| scenario | attempts | first attempt to terminal |
|---|---|---|
| `dissolve-work-limit` | 3 | 95.7 s |
| `join-both-sides` | 3 | 91.4 s |
| `buffer-work-limit` | 3 | 95.2 s |
| `single-geometry` | 3 | 91.3 s |
| `elapsed-time-limit` | 3 | 120.8 s (three 8 s deadlines) |

Dismissal stops the running join in 2.28 seconds (CPU 98.2% to 5.9%), all 2,130
serving probes pass (maximum 8.23 s), there is no OOM kill and cleanup completes.
Serving availability and cleanup therefore already hold on the pinned candidate;
the retry multiplication is the only gap, and the next re-pin must include
`927af8fbc` for this harness to pass.

The runtime correction makes input, resource-limit and deadline refusals terminal
(`IsRetryable = false`) in the layer and enrichment executors; transient source-read
failures keep their retry budget. #4629 closes only once a manifest-pinned image
that contains this correction passes this harness. Managed topology is not
preemptible inside a call: admission limits its input work, and callers observe
cancellation between calls. These receipts do not claim a measured worst-case
runtime for every admitted geometry, an isolated worker kill guarantee, or the
shared crash/retry/storage qualification owned by #3848/#3852.
