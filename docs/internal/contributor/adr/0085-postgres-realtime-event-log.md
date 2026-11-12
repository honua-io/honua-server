# ADR-0085: Cross-node realtime runs on a PostgreSQL event log behind one fan-out provider

## Status
Proposed (2026-11-11). Founder-approved design, 2026-11-11. Target release 2026.1-rc.3. Builds on
[ADR-0081](0081-postgres-job-substrate.md) and [ADR-0082](0082-postgres-control-plane-state.md).
It replaces ADR-0082 rows D7 (the planned `feature_change_events` table) and E5 to E8 (realtime
per node only), and amends one ADR-0082 house rule (no `LISTEN/NOTIFY`) with the single exception
in Decision 3. Contract and acceptance tests: the
[cross-node realtime specification](../specs/realtime-cross-node-rc3.md).

## Context
The certified deployment is one ECS Fargate service with two or more tasks, RDS PostgreSQL with
PostGIS, S3, and CloudFront in front of an internal load balancer. There is no Redis, and none will
be added. Four realtime surfaces used Redis pub/sub or the SignalR Redis backplane to reach clients
connected to another task: the admin live hub, map collaboration sessions, feature-change streams
and SensorThings observation streams. Without Redis each surface delivers only what its own task
produced, so they are advertised as Experimental, and collaboration edits fail closed whenever
`Deployment:Mode` is `MultiNode`.

Each surface has its own seam (`IFeatureChangeEventPublisher`, `IObservationChangeEventPublisher`,
`ICollaborationSessionBackplane`, `IAlertNotificationBroadcaster`, `IMcpNotificationPublisher`,
the SignalR `IHubContext<AdminHub>`), and no shared fan-out abstraction. Alerts, MCP notifications
and job progress have the same problem without having been labelled realtime.

The data already needed is mostly in PostgreSQL. `honua.feature_changes` is filled by a row trigger
on the feature tables, so edits made directly in the database are captured. Its `generation` is
allocated under a transaction-scoped advisory lock that is held until commit, so generation order is
commit order. Saved-map operations are rows with a per-map cursor. Observations, alert events and
jobs are rows.

## Decision
1. **PostgreSQL is the realtime event log.** Every event that clients can resume is a row with a
   sequence that is monotonic per family and assigned in commit order. Feature changes keep
   `honua.feature_changes` as their log (sequence `generation`). The other families share one table,
   `honua.realtime_events`, keyed `(family, seq)`. A per-family head row is incremented in the
   appending statement, so the row lock orders commits and the sequence has no gaps. Where a
   database edit must be captured, a trigger writes the log row. Otherwise the application appends
   in the same transaction as the change.
2. **One fan-out provider.** Features publish and subscribe through `IRealtimeFanoutProvider` only.
   Two implementations ship. `InProcessRealtimeFanoutProvider` is for one node: it wakes on local
   commits and polls slowly for trigger-fed rows. `PostgresRealtimeFanoutProvider` is for several
   nodes: every task tails each family's log once and dispatches to the clients connected to that
   task, and clients may connect to any task. `Realtime:Provider` is `auto`, `in-process` or
   `postgres`, and `auto` selects `postgres` when `Deployment:Mode` is `MultiNode`. A managed
   provider can be added later behind the same interface without changes to features. None is
   built now.
3. **Wake-up by `NOTIFY`, data by query.** Each appending transaction issues a payload-free
   `pg_notify('honua_rt_<family>', '')`, which PostgreSQL folds to one notification per channel per
   transaction. Each task holds **one dedicated listener connection**: not pooled, not through RDS
   Proxy, used for nothing but `LISTEN`. On a wake-up the task reads the new rows from the table. A
   safety poll every 10 s covers lost notifications, and a 1 s poll takes over while the listener is
   down. This listener is the only exception to the ADR-0082 rule against `LISTEN/NOTIFY`. Its
   limits are stated in the specification. Pooled connections never `LISTEN`.
4. **Resume by sequence.** The client sends the last sequence it saw: SSE `Last-Event-ID`, the
   existing `cursor` query parameter, or the collaboration `resumeFrom` parameter on the WebSocket.
   The server replays the gap from the table and then continues live without duplicates on that
   connection. Retention is per family and configurable. A position older than retention receives
   a typed `reset`, and the client takes a fresh snapshot.
5. **Transports.** One-way feeds use Server-Sent Events: feature changes, job and layer updates, the
   admin live view and SensorThings observations. Map collaboration uses WebSockets. Collaboration
   edits are committed to PostgreSQL under the existing base-cursor and head-row checks, so
   conflicts are resolved in the database and never in a node's memory.
6. **The admin live view moves from SignalR to SSE.** The hub, its Redis backplane and the
   `/hubs/admin` route are deleted. The reasons are in the specification. In short: the traffic is
   one-way, SSE can resume from the log and SignalR cannot, a cross-node `HubLifetimeManager` would
   reimplement group and user routing for four one-way groups, and SignalR's non-WebSocket transports
   need sticky sessions that the load balancer does not provide.
7. **Authorization per subscriber, per event.** Each event goes through the same access evaluation
   as a normal read for the subscribing principal: layer visibility, row policies and field masks.
   Permission, catalog and credential changes are logged events of their own (`security`,
   `catalog`). On receiving one, every task re-evaluates its open subscriptions and closes streams
   whose credential was revoked, so a change takes effect within the latency target. A 30 s
   re-evaluation is the backstop. It replaces today's one-second credential re-check on every open
   stream.
8. **CDN and load balancer.** Realtime paths get uncached CloudFront behaviours with compression
   off. The load balancer idle timeout is 120 s. Servers send a heartbeat every 15 s and drain
   connections, with a retry hint, when a task stops. The exact template changes are in the
   specification.
9. **Scale envelope.** The target is hundreds to low thousands of concurrent subscribers per
   install. Database load grows with tasks and families, not with subscribers. Each task makes one
   read per family per wake-up and holds one extra connection.

## Alternatives considered
- **Keep realtime per node (ADR-0082 E5 to E8).** Clients see a random subset of events, and
  collaboration editing stays closed on the certified topology.
- **`LISTEN/NOTIFY` with payloads as the transport.** The 8000-byte payload limit means events would
  still need the table, and anything sent while a listener is disconnected is lost. The table is
  needed for resume regardless.
- **A listener on every pooled connection, or through RDS Proxy.** `LISTEN` pins proxy sessions, and
  a pooled connection is reused by other work. One dedicated connection per task is bounded and
  observable.
- **Polling only.** It meets correctness but not the latency target unless it polls hard. Polling
  stays as the fallback.
- **A custom SignalR `HubLifetimeManager` over the log.** Rejected in Decision 6.
- **A separate `feature_change_events` table (ADR-0082 D7).** It would be a second copy of
  `feature_changes` with its own cursor, and it would miss database edits that bypass the outbox.
- **A managed AWS fan-out service now.** It is not needed at the envelope above and would add a
  dependency to the listing. The provider interface keeps it possible.

## Consequences
- Realtime works across tasks on the certified topology with no service beyond PostgreSQL.
  Collaboration editing is enabled on `MultiNode`. Promotion from Experimental is a separate tier
  decision once the acceptance suite passes.
- Each task holds one more database connection, which needs a direct (non-proxy) endpoint.
  `pg_notification_queue_usage()` becomes a monitored value.
- Writers in the same family serialize on the family head row until they commit. That is cheap at
  the envelope but sets a ceiling on append rate per family. Feature writers already serialize on
  the generation advisory lock.
- Wire changes: feature-stream positions become `features:<generation>`, and a bare numeric cursor
  from an earlier build receives `reset` (`cursor-epoch`). The SensorThings observation stream gains
  resume. `/hubs/admin` and `backplaneEnabled` are removed, and `GET /api/v1/admin/live` replaces
  them. Console changes in the same release.
- Deleted: `RedisCollaborationSessionBackplane`, the Redis channels, backlog and recovery timer in
  `FeatureStreamSessionManager` and `ObservationStreamSessionManager`, the SignalR Redis backplane
  and hub, `InMemoryFeatureChangeEventStore`, `FeatureChangeRetryQueue`, and the broadcast step of
  `FeatureStreamPublisher`.
