# Separate realtime load client

`scale-client.mjs` uses Node 22 native HTTP streams and runs separately from the
two Kestrel tasks. It exercises the acceptance fixture's editable `test` service,
layer `0`, alternating writes and subscriptions between the two targets. Each
subscriber must receive every marked edit exactly once, in generation order.
The client creates features; use an isolated acceptance dataset.

Create a private JSON profile outside the repository with mode `0600`:

```json
{
  "targets": [
    { "url": "http://127.0.0.1:18081", "headers": { "X-API-Key": "PRIVATE_KEY" } },
    { "url": "http://127.0.0.1:18082", "headers": { "X-API-Key": "PRIVATE_KEY" } }
  ],
  "subscribers": 2000,
  "editsPerSecond": 50,
  "seconds": 600,
  "settleMilliseconds": 60000
}
```

Supply any tenant, schema or test-scope headers required by the fixture in each
target's `headers` object. The tasks must already be running with PostgreSQL
fan-out, editing enabled, sufficient admission limits and a shared dataset.
For a remote fixture, forward each existing task's loopback listener over SSH
to a different local port; run Node on the client machine. Distinct origins are
checked, but they alone do not prove distinct server tasks. This script does
not start fixtures, provision infrastructure or change limits.

Run the client and its local protocol tests:

```bash
node scripts/conformance/realtime/scale-client.mjs /private/loaded.json /private/loaded-result.json
node --test --test-timeout=10000 scripts/conformance/realtime/scale-client.test.mjs
```

Run a second profile with `subscribers: 0`, keeping the same write rate, duration,
tasks and database configuration, to collect the baseline. Measure database CPU
for both runs externally. Record one listener per task, reconnect counts and
task identities alongside the two result files. The client omits target URLs
and credentials from results; keep private profiles out of logs and artifacts.

`deliveryPassed` requires complete ordered delivery, request-to-event p95 at
most one second, and writer scheduling lag at most one second. Request-to-event
time starts before each HTTP edit and therefore includes the write itself; it
is a conservative bound on commit-to-event time. The zero-subscriber result
checks the writer only. Client CPU is expressed as percent of one CPU core and
may exceed 100. Event-loop delay and scheduling lag identify client contention;
neither replaces server or database measurements.

`scaleGatePassed` remains false: listener counts, independent task identity and
the database CPU delta (at most ten percentage points) need external evidence.
A delivery pass also does not establish the live CDN idle, WebSocket or rolling
deployment journeys. The previous colocated load failure remains unresolved
until the separate-client run and external measurements distinguish its cause.
