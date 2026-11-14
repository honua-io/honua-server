# Realtime stream operations

The shared lifecycle closes admission as soon as application shutdown starts. Readiness
then returns 503 with `Cache-Control: no-store`; liveness remains available. Transport
handlers must register each session with `IRealtimeStreamLifecycle`, reject a null
registration with 503 and `Retry-After: 2`, and dispose the session when it closes.

The default drain budget is `Realtime:ShutdownDrain` = 20 seconds. Sessions receive a
staggered cancellation signal during that budget. An SSE handler sends its terminal
retry frame using the session's randomized 1000–5000 ms retry value; a WebSocket handler
sends service-restart close code 1012. Remaining sessions are aborted at the deadline.
The log tail and dedicated listener stop after this drain. Handlers use the shared
15-second heartbeat interval, and SSE responses disable proxy buffering and transformation
with `X-Accel-Buffering: no` and `Cache-Control: no-store, no-transform`.

Transport integration is required: registering the shared service alone does not add
heartbeats, terminal frames, or authorization checks to a handler. Verify every transport
on a live rolling restart before treating the lifecycle journey as passed.

## Signals and recommended alarms

The meter is `Honua`. Prometheus normalizes dots to underscores; counters also acquire
`_total`, and histograms expose `_bucket`, `_count`, and `_sum`. Numeric durations below
are seconds. Avoid tenant, user, subscription, or event identifiers as metric labels.

| Instrument | Meaning and useful alarm |
| --- | --- |
| `honua.realtime.subscribers` | Active registered sessions by family and transport. Alert above 80% of the configured per-task cap; compare on the same task, rather than summing across tasks. |
| `honua.realtime.log.head` | Last observed committed sequence by family. |
| `honua.realtime.dispatch.position` | Last dispatched sequence by family. |
| `honua.realtime.dispatch.lag.events` | Last observed head minus dispatch position. Sustained growth requires investigation even if the listener is connected. |
| `honua.realtime.dispatch.lag.seconds` | Age of the oldest observed undispatched event. Alert when the p99 exceeds five seconds over five minutes. Evaluate within each task and family. |
| `honua.realtime.delivery.latency` | Commit-to-flushed-frame histogram by family and transport. Alert when p95 exceeds one second for ten minutes. Record only after the frame is flushed. |
| `honua.realtime.listener.connected` | Dedicated listener state, 0 or 1. Alert on 0 for two minutes on a task configured for the PostgreSQL provider. An in-process provider has no dedicated listener. |
| `honua.realtime.listener.reconnects` | Successful reconnects following failure. Use increases to correlate connection instability with lag. |
| `honua.realtime.wakeups` | Wake-ups by `source`: `notify`, `safety-poll`, or `degraded-poll`. Polling preserves delivery when notifications are unavailable. |
| `honua.realtime.resets` | Reset frames written, by family, transport, and reason. A reset requires the client to resynchronize its snapshot. |
| `honua.realtime.slow_consumer.closed` | Subscriptions closed when their bounded channel filled. Alert when five-minute closures exceed 1% of active subscribers, accounting for periods with no subscribers. |
| `honua.realtime.frames.dropped` | Frames rejected by a full bounded channel. Correlate with slow-consumer closures and client reconnects. |
| `honua.realtime.authz.frames_filtered` | Frames filtered by authorization, by family and transport. |
| `honua.realtime.authz.streams_revoked` | Sessions closed after access revocation, by family and transport. |
| `honua.realtime.notify_queue.usage` | Database-wide PostgreSQL notification queue fraction. Alert above 0.25. The maintenance lease holder samples once per minute; missing samples require checking the sampler warning log and lease/database health. |

A disconnected listener alone does not establish delivery failure: the tail continues
polling the durable log. Check dispatch lag, delivery latency, and polling wake-ups
together. A connected listener alone does not establish healthy delivery either.

These are recommended thresholds, not provisioned alarms. Configure them in deployment
infrastructure and verify their routing separately. The metric inventory is guarded by
`eng/telemetry/metric-name-contract.v1.json`.
