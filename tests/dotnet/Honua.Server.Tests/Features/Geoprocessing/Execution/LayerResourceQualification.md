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
arrays, the original `objectid` attributes and the expected process/count metadata,
read back over the client-facing contract (`GET /ogc/processes/jobs/{id}/results`).
The admin console job-artifacts view is asserted to list exactly one artifact for the
same job, but it is no longer the source of the payload: since #4845 it redacts inline
`data:` references, so it attests that the artifact exists without carrying its bytes.
The oversized ring and each
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

`layer-resource-nightly-4f2cb3f9-receipt.json` runs the same harness on the first
published Native AOT image that contains #4881,
`ghcr.io/honua-io/honua-server@sha256:bffb4fa8df43c480187aa16567a59f55187d50818e90f92d680b21ef05dee260`
(amd64 AOT image of nightly run 34923885668, `org.opencontainers.image.revision` =
`4f2cb3f97e92d558d8793c454139267b5c484dd2`). It **passes** every scenario:

- Each deterministic refusal is terminal after exactly one attempt: dissolve 0.33 s,
  join 0.44 s, buffer 0.23 s, oversized single geometry 0.16 s.
- The elapsed-time failure runs once and lasts 8.47 s against the 8 s deadline, inside
  the one-attempt bound.
- Dismissal stops the running join in 2.03 s, container CPU 97.4% to 6.4%, and the job
  is still `dismissed` with one attempt after the 45 s observation window.
- All 340 serving probes pass (maximum 3.25 s), there is no OOM kill, and cleanup passes.

This is the manifest-pinned published image the acceptance asks for. What remained at
the time was a release re-pin whose source includes `927af8fbc`; the candidate pinned on
2026-09-15 (`548b7a526`) predated it. That re-pin landed on 2026-09-16, and the receipt
below replays the acceptance on it.

`layer-resource-candidate-8862065-receipt.json` runs this harness on the 2026.1
candidate pinned on 2026-09-16 (release PR #354),
`ghcr.io/honua-io/honua-server@sha256:ec8d7915ca72ef3a8d4ffb4719f7711496f5046f538acf7e4fc2d7e6e2d028aa`
(amd64 Native AOT member of index `sha256:0b16046533e5330ecdd48255c06b5397e869191299e1e5e8cc7b4b2ded60b388`,
`org.opencontainers.image.revision` = `886206527cc97bad1bbaa5fa6358910ebc45e9c0`, which
contains `927af8fbc`). One CPU, 1 GiB, production OGC execution with PostGIS reads and
Redis jobs, server restarted before execution. It **passes** every scenario:

| scenario | attempts | first attempt to terminal | observed outcome |
|---|---|---|---|
| `bounded-passthrough` | — | 3.81 s wall | exact original ordinates and `objectid`s, `featureCount=2`, `processId=generalization.dissolve` |
| `dissolve-work-limit` | **1** | 0.07 s | `managed topology for 10 by 10 vertices exceeds Geoprocessing:Executors:MaxTopologyWork=99; stopped before computation` |
| `join-both-sides` | **1** | 0.10 s | same ceiling charged across both join sides |
| `buffer-work-limit` | **1** | 0.10 s | buffer intermediates charged: 74 by 74 vertices |
| `single-geometry` | **1** | 0.04 s | `layer 946291 contains a geometry with 1001 vertices, exceeding the configured limit of 100` |
| `elapsed-time-limit` | **1** | 8.11 s against the 8 s deadline | `exceeded Geoprocessing:Executors:MaxLayerExecutionSeconds=8` |
| `dismiss-running-join` | 1 | dismissed 0.06 s after the `DELETE` (request itself 0.03 s) | CPU 101.4% to 6.2% within 1.99 s; still `dismissed` with one attempt after the 45.0 s observation |

All 357 serving probes passed with no failures (maximum 3.28 s), the container was
never OOM-killed and ended `running`/`healthy`, and cleanup passed. This is the
exact-candidate evidence #4629's last acceptance criterion asks for.

The bounded-passthrough scenario reads its output from
`GET /ogc/processes/jobs/{id}/results` rather than from the admin console's artifact
identifier. Earlier receipts decoded a `data:application/geo+json;base64,` reference
that the console returned verbatim in `artifactId`; #4845 replaced that field with a
salt-free SHA-256 digest for references that are not safe provider links, so the
console now reports `availability: Redacted` for inline results. The payload itself
was never lost — the OGC results document carries the full feature collection, which
is the contract a client and the SDKs actually read. Reading it there is a stricter
oracle than the console path it replaces: it additionally asserts the output media
type, the FeatureCollection envelope and the per-feature attributes.

Receipts record the SHA-256 of the harness that produced them. The receipts through
`layer-resource-nightly-4f2cb3f9-receipt.json` were produced by harness
`c4a41df326806869033abc2f1b65c70d0dd3b85c821fbfde07de3e28c01a4fca`;
`layer-resource-candidate-8862065-receipt.json` was produced by
`8bc3bc322519042af58a33129ca8f9cfee35b8c75f33f37fe9289ab61a01f86f`, which differs only
in how bounded passthrough reads its output. Every limit, attempt-count, deadline,
dismissal, serving and cleanup assertion is unchanged.

The runtime correction makes input, resource-limit and deadline refusals terminal
(`IsRetryable = false`) in the layer and enrichment executors; transient source-read
failures keep their retry budget. #4629 closed on
`layer-resource-candidate-8862065-receipt.json`, the first manifest-pinned candidate
that contains this correction and passes this harness. Managed topology is not
preemptible inside a call: admission limits its input work, and callers observe
cancellation between calls. These receipts do not claim a measured worst-case
runtime for every admitted geometry, an isolated worker kill guarantee, or the
shared crash/retry/storage qualification owned by #3848/#3852.
