# Cross-node realtime without Redis (rc.3)

Status: Proposed. Decision record: [ADR-0085](../adr/0085-postgres-realtime-event-log.md). Release
2026.1-rc.3. Paths are relative to the repository root and refer to `trunk` at `5d7707d0ef`.
Defaults are named in each section and are all configurable under `Realtime:`.

## 1. Goals and limits
- Any client connected to any task receives every event it is authorized to see. The event may
  have been committed by any task or written directly in the database.
- Target latency from commit to a frame written on another task: **p95 ≤ 500 ms and p99 ≤ 1 s**
  while the listener is healthy, and at most the degraded poll interval (1 s) plus 500 ms while it
  is not.
- A reconnecting client loses nothing that is still within the family's retention. It may receive
  events again across a reconnect, and it deduplicates them by id.
- Envelope: hundreds to low thousands of concurrent subscribers per install, on two to eight tasks.
- Out of scope: multi-region, read replicas as a source, and a managed fan-out service. The provider
  interface (section 4) is the extension point for those.

## 2. Current components and their replacements
| Component (trunk) | Cross-node mechanism today | Replacement |
|---|---|---|
| Feature streams: `src/Honua.Server/Features/Streaming/FeatureStreamSessionManager.cs` (Redis channel `featurechange:stream:broadcast`, a 1,024-entry backlog, a 5 s recovery timer), `FeatureStreamPublisher.cs` (store append, then local broadcast), `FeatureStreamEndpoints.Sse.cs` and `.WebSocket.cs` | Redis pub/sub when a multiplexer is registered; otherwise the task's own events only | The session manager subscribes to the `features` family through the provider. The Redis fields, backlog, timer and `ClusterBroadcastBacklogSnapshot` are deleted. `FeatureStreamPublisher` stops broadcasting and keeps webhook and sink dispatch |
| Feature event store: `src/Honua.Hosting/Features/Events/InMemoryFeatureChangeEventStore.cs` (Redis Lua cursor, 7 days, 20,000 events), registered in `src/Honua.Server/Startup/FeatureEventsAndStreamingRegistration.cs` | Redis keys, or process memory without Redis | `PostgresFeatureChangeEventStore` reads `honua.feature_changes` joined to `honua.feature_change_outbox` on `event_id`. Its cursor is `generation` and `AppendAsync` is removed. This replaces ADR-0082 D7 |
| `src/Honua.Server/Features/Infrastructure/Events/FeatureChangeRetryQueue.cs`, `src/Honua.Hosting/Features/Events/FeatureChangeRetryBackgroundService.cs` | Redis or `IDistributedCache` retry of failed appends | Deleted (ADR-0082 D8). The trigger writes the log row in the edit transaction, so there is no append left to retry |
| Webhook dispatcher: `src/Honua.Hosting/Features/Events/FeatureChangeWebhookDispatcher.cs` | Redis cursor and lease | Unchanged in purpose (ADR-0082 D9). Its `stream_cursors` row holds a generation, and on first start it is initialised to the current head |
| SensorThings: `src/Honua.Protocols/SensorThings/Streaming/ObservationStreamSessionManager.cs` (channel `sta:observation:stream:v2:broadcast`), `ObservationStreamScope.cs` (`ObservationStreamPublisher`), `ObservationStreamEndpoints.cs` (refuses `cursor` and `Last-Event-ID`) | Redis pub/sub, best effort | A statement trigger on `sta_observation` appends to the `observations` family. The manager subscribes through the provider, and the endpoint accepts resume. `IObservationChangeEventPublisher` is deleted because the trigger is the publisher |
| Collaboration: `src/Honua.Server/Features/Collaboration/Sessions/RedisCollaborationSessionBackplane.cs`, `ICollaborationSessionBackplane.cs` (`NullCollaborationSessionBackplane`), `InMemoryCollaborationSessionService.cs`, `Operations/SavedMapOperationAppendCoordinator.cs` (`CanAcceptEdits` is false on multi-replica because no backplane reports ordered delivery) | Redis pub/sub for presence and cursors; edits closed on `MultiNode` | `ICollaborationSessionBackplane` is deleted. Operations go through the `collab-ops` family (a trigger on `saved_map_operations`) and presence through `collab-presence` (batched append). `CanAcceptEdits` reads the provider's `OrderedCrossNode` capability |
| Admin live view: `src/Honua.Server/Features/Admin/AdminRealtimeHub.cs` (SignalR with `AddStackExchangeRedis`, groups refused without Redis), `AdminRealtimeBroadcaster.cs`, `ProposalNotifier.cs`, `RealtimeOperationTransitionListener` | SignalR Redis backplane | `GET /api/v1/admin/live` (SSE) over the `admin` family. The hub, `/hubs/admin`, the SignalR packages and `AdminRealtimeCapabilities` are deleted (section 9.3) |
| Alerts: `src/Honua.Server/Features/Alerts/WebSocketAlertDeliverySink.cs` (`InMemoryAlertNotificationBroadcaster`), stream at `GET /api/v1/admin/operations/streaming/alerts` (WebSocket) | None: the dispatching task only | The WebSocket delivery sink appends to the `alerts` family. The stream endpoint subscribes through the provider. `IAlertNotificationBroadcaster` is deleted |
| MCP: `src/Honua.Ai/Features/Protocols/Mcp/Mcp/McpNotificationPublisher.cs` (enqueues to `McpSessionManager` sessions held on this task), callers `McpJobProgressBridge`, `McpWorkflowViewChangeNotifier`, `ServicePublishExecutor`, `LayerPublishJobWork` | None: progress produced on another task is lost | `IMcpNotificationPublisher` keeps its signature and appends to the `mcp` family (scope: session id, or `*` for list-changed). The task that holds the session's stream delivers |
| Job and layer updates | None (clients poll the job status routes) | New SSE feeds `GET /api/v1/streaming/jobs` and `GET /api/v1/streaming/layers` (section 9) |
| Stream credential re-check: `src/Honua.Hosting/Features/Authentication/LiveStreamAuthorizationFilter.cs` (`RevalidationInterval` 1 s on every open stream) | n/a | Event-driven revocation through the `security` family, plus a 30 s backstop (section 8). At 2,000 streams the current rule costs about 2,000 credential reads per second against PostgreSQL |

## 3. Event families
| Family | Source of rows | Written by | `scope_key` | Retention (default) |
|---|---|---|---|---|
| `features` | `honua.feature_changes` (existing) | Row trigger `trigger_track_feature_changes` (migrations 111, 124, 137) | n/a: filtered by `layer_id` | Owned by replication, never swept here. Resume window 24 h and at most 10,000 events |
| `collab-ops` | `realtime_events` | Row trigger on `saved_map_operations` insert | saved-map id | 24 h |
| `collab-presence` | `realtime_events` | Application, batched (section 5.4) | saved-map id | 2 min |
| `observations` | `realtime_events` | Statement trigger with transition table on `sta_observation` insert, one row per datastream per statement, split at 1,000 observations | datastream id | 1 h |
| `alerts` | `realtime_events` | `WebSocketAlertDeliverySink` inside alert dispatch | service id | 7 d |
| `jobs` | `realtime_events` | Statement trigger on `execution_jobs` update when `status` changes | job id | 24 h |
| `catalog` | `realtime_events` | Row trigger on `metadata_v2_current` update when `etag` changes | environment | 24 h |
| `admin` | `realtime_events` | Application: `ProposalNotifier`, `RealtimeOperationTransitionListener`, the `OpsHealthFlushSignal` subscriber | topic (`proposals`, `ops-health`, `operate-events`, `deploy-operations`) | 24 h |
| `security` | `realtime_events` | Statement triggers (section 8.2). Internal only, never sent to clients | principal id, credential hash or `*` | 1 h |
| `mcp` | `realtime_events` | `McpNotificationPublisher` | MCP session id or `*` | 10 min |

### 3.1 Payloads
Log rows carry what dispatch needs to authorize and frame an event without another read.
- `features`: the existing columns `generation`, `layer_id`, `objectid`, `public_objectid`,
  `operation`, `version_id`, `actor`, `source`, `operation_name`, `source_id`, `event_id`,
  `pre_*` (121) and `committed_*` (137). Dispatch keeps only rows where `version_id IS NULL`, as
  `PostgresAlertChangeReader` does. Row-policy predicates and field masks are evaluated against
  `committed_attributes`, or against `pre_attributes` for a delete. The outbox row, when there is
  one, supplies `service_id`, `protocol` and `request_id`. A database edit has no outbox row:
  `protocol` is `database`, the event id is `features:<generation>`, and the service comes from the
  catalog's layer binding.
- `collab-ops`: `{operationId, mapId, serverCursor, baseCursor, actorId, kind}`. The operation
  payload is read from `saved_map_operations` by `(map_id, server_cursor)` in the same batch query.
- `collab-presence`: `{sessionId, participantId, state: joined|left|moved, pointer?, selection?}`,
  latest per participant per batch.
- `observations`: `{datastreamId, observations: [{id, phenomenonTime, resultTime, result,
  featureOfInterestId}]}`.
- `alerts`: the existing `AlertEventEnvelope`.
- `jobs`: `{jobId, kind, status, requesterId, updatedAt}`. No specification or result.
- `catalog`: `{environment, etag, previousEtag}`.
- `admin`: the existing `ProposalRealtimeEvent`, `OpsHealthSnapshotResponse` with the task id,
  `OperateEventResponse` and `DeployOperationRealtimeEvent`.
- `security`: `{kind, subject}`, section 8.2.
- `mcp`: the serialized JSON-RPC notification produced today.

A payload is at most 64 KiB. An append above that limit is refused with an error, which is a defect
in the writer.

## 4. Fan-out provider
`Honua.Core` holds the abstraction. Implementations live in `Honua.Server` beside the realtime
feature slice and use `JobStoreDatabase` (ADR-0082).

```csharp
/// <summary>The only seam realtime features use to publish and subscribe.</summary>
public interface IRealtimeFanoutProvider
{
    RealtimeProviderCapabilities Capabilities { get; }

    /// <summary>Appends one event; joins <paramref name="transaction"/> when given.</summary>
    ValueTask<RealtimePosition> AppendAsync(RealtimeEventDraft draft, DbTransaction? transaction, CancellationToken cancellationToken);

    /// <summary>Queues a best-effort event that is appended in the next batch (presence only).</summary>
    void Enqueue(RealtimeEventDraft draft);

    /// <summary>Starts a subscription that replays after <c>request.After</c> and then follows the log.</summary>
    IRealtimeSubscription Subscribe(RealtimeSubscriptionRequest request);

    ValueTask<RealtimeLogWindow> GetWindowAsync(RealtimeFamily family, CancellationToken cancellationToken);
}

public sealed record RealtimeProviderCapabilities(bool CrossNode, bool OrderedPerFamily, bool Resumable);
```

- `RealtimeSubscriptionRequest` carries the family, the scope keys (empty means all), `After` (a
  sequence or null for live only), a bounded `ChannelWriter<RealtimeDelivery>` owned by the
  transport, and a filter delegate supplied by the feature for authorization and matching.
- `RealtimeDelivery` is `Event(seq, kind, payload)`, `Reset(reason, head)` or `Heartbeat`. Reasons
  for a reset: `retention`, `cursor-epoch`, `too-far-behind`, `slow-consumer`.
- **In-process provider** (`Realtime:Provider=in-process`, the `auto` choice on a single node):
  the same log tables and readers, woken by a local signal after each append and after each feature
  commit. A 2 s poll picks up trigger-fed rows written by other processes. It holds no listener
  connection. Capabilities: `CrossNode=false`, `OrderedPerFamily=true`, `Resumable=true`.
- **PostgreSQL provider** (`postgres`, the `auto` choice when `Deployment:Mode=MultiNode`):
  section 6. Capabilities: all true.
- A future provider (a managed service, or another cloud) implements the same interface. Endpoints,
  stores and features depend on the interface only. An architecture test forbids references to the
  concrete providers outside `Startup/RealtimeRegistration.cs`.

## 5. Tables, indexes and append
New migration `NNN_CreateRealtimeEventLog.sql`. It is expand-only, and each statement is idempotent.

```sql
CREATE TABLE IF NOT EXISTS $HonuaSchema$.realtime_log_heads (
    family text COLLATE "C" PRIMARY KEY,
    head   bigint NOT NULL DEFAULT 0
);
INSERT INTO $HonuaSchema$.realtime_log_heads (family) VALUES
    ('collab-ops'), ('collab-presence'), ('observations'), ('alerts'), ('jobs'),
    ('catalog'), ('admin'), ('security'), ('mcp')
ON CONFLICT DO NOTHING;

CREATE TABLE IF NOT EXISTS $HonuaSchema$.realtime_events (
    family     text COLLATE "C" NOT NULL REFERENCES $HonuaSchema$.realtime_log_heads (family),
    seq        bigint NOT NULL,
    scope_key  text COLLATE "C" NOT NULL,
    kind       text NOT NULL,
    payload    jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL,
    PRIMARY KEY (family, seq)
);
CREATE INDEX IF NOT EXISTS ix_realtime_events_scope
    ON $HonuaSchema$.realtime_events (family, scope_key, seq);
CREATE INDEX IF NOT EXISTS ix_realtime_events_expires
    ON $HonuaSchema$.realtime_events (expires_at);
```

### 5.1 Append
`honua.realtime_append(p_family text, p_rows jsonb, p_ttl interval) RETURNS bigint` takes an array
of `{scope_key, kind, payload}` and runs one statement:

```sql
WITH h AS (
    UPDATE honua.realtime_log_heads SET head = head + jsonb_array_length(p_rows)
    WHERE family = p_family RETURNING head)
INSERT INTO honua.realtime_events (family, seq, scope_key, kind, payload, expires_at)
SELECT p_family, h.head - jsonb_array_length(p_rows) + r.ordinality, r.value->>'scope_key',
       r.value->>'kind', r.value->'payload', now() + p_ttl
FROM h, jsonb_array_elements(p_rows) WITH ORDINALITY AS r;
```

It then runs `PERFORM pg_notify('honua_rt_' || p_family, '')`. Triggers call the same function. TTLs
come from `Realtime:Retention:<family>`. Triggers read them from a one-row settings table that is
written at startup, so a trigger never hard-codes a TTL.

### 5.2 Ordering guarantees
- `features`: one total order by `generation`. Allocation runs under `pg_advisory_xact_lock(144047712, 0)`,
  which is held to commit, so no row with a lower generation becomes visible after a higher one.
  Generations have gaps (rollbacks, branch rows), so positions mean "after N", never "the Nth".
- `realtime_events` families: one total order per family by `seq`. The head row lock is held to
  commit, so commit order equals `seq` order and there are no gaps except those left by retention.
  The same row lock serializes appending transactions of one family, which is why triggers append
  once per statement and application appends happen last in their transaction.
- `collab-ops`: also totally ordered per map by `server_cursor`, under the existing head-row
  `FOR UPDATE` in `PostgresSavedMapOperationLogRepository`. That is the cursor clients see.
- `collab-presence`: latest wins per participant. It is not resumable. A (re)connect receives a
  presence snapshot.
- There is no order across families.
- Delivery: in order and without duplicates within one connection, using the existing
  `FeatureStreamDeliveredCursorWindow` hand-off from replay to live. At-least-once across
  reconnects.

### 5.3 Retention and cleanup
`realtime_events` registers an `IExpiredRowSweep` (`src/Honua.Jobs/Features/ControlPlane/IExpiredRowSweep.cs`)
with the existing `SharedStateRetentionSweeper`. Every 10 minutes it deletes at most 1,000 rows per
batch by `ix_realtime_events_expires`, with `FOR UPDATE SKIP LOCKED`. No new sweeper is added.
`feature_changes` is not swept here. The `features` resume window is a read rule: a position older
than `Realtime:Retention:features` (24 h), or more than `Realtime:ResumeMaxEvents` (10,000) behind
the head, receives `reset`. The oldest retained position of a family is `min(seq)` (index-only on
the primary key), cached per task for 30 s.

### 5.4 Presence batching
`Enqueue` keeps the latest presence state per `(map, participant)` in memory. Every 250 ms
(`Realtime:PresenceFlushInterval`), if anything changed, the task appends all of it in one
`realtime_append` call. Each task issues at most four statements per second for presence,
regardless of participant count. Pointer updates from a client are coalesced to the latest value.

## 6. PostgreSQL provider: tailing and the listener
### 6.1 Head probe and reads
Each task keeps `dispatched[family]`. A wake-up or a poll runs one probe:

```sql
SELECT family, head FROM honua.realtime_log_heads
UNION ALL
SELECT 'features', COALESCE((SELECT generation FROM honua.feature_changes
                             ORDER BY generation DESC LIMIT 1), 0);
```

Every family whose head is ahead of `dispatched` is read in batches of `Realtime:ReadBatchSize` (500):
`WHERE family = @f AND seq > @dispatched ORDER BY seq LIMIT @n`, or for features
`WHERE generation > @dispatched AND version_id IS NULL ORDER BY generation LIMIT @n`, plus the
outbox join. Each batch is dispatched to the task's subscriptions, and then `dispatched` advances.
At startup `dispatched` is the head, so a task never replays history to live subscribers. Replay is
per subscription (section 9.1).

### 6.2 The listener (the ADR-0082 exception)
- One `NpgsqlConnection` per serving process, from its own `NpgsqlDataSource` built from
  `ConnectionStrings:RealtimeListener`. The default is `DefaultConnection` with `Pooling=false`,
  `Keepalive=15`, `Tcp Keepalive=true` and `Application Name=honua-realtime-listener`. It runs
  `LISTEN honua_rt_<family>` for every family and then only `WaitAsync`. It never opens a
  transaction and never runs another statement.
- It must reach the instance or cluster writer endpoint directly. If the host name contains
  `.proxy-` (an RDS Proxy endpoint), startup logs a warning and the provider runs poll-only at the
  degraded interval. The certified CloudFormation stack has no RDS Proxy, and the listener uses the
  stack's database endpoint.
- It runs only where `ControlPlaneLambdaConnectionRelease.HostsDatabasePollingLoops` is true. A
  frozen Lambda must not hold it.
- Limits, stated in the ADR and the operator guide:
  - one extra connection per task;
  - a notification is lost while the listener is disconnected, which the catch-up read covers;
  - notifications are not delivered on read replicas;
  - PostgreSQL serializes the commit of notifying transactions on one global lock, which sets a
    ceiling on notifying transactions per second for the install;
  - a listener that stops consuming lets the notification queue (8 GB) fill. NOTIFY then fails, and
    with it the writing transaction. The listener therefore consumes continuously, and
    `pg_notification_queue_usage()` is alarmed (section 11).

### 6.3 Lifecycle
- **Start**: open, `LISTEN`, then probe once. That probe closes the window between startup and
  `LISTEN`.
- **Wake-up**: notifications are coalesced. Any number received within 20 ms trigger one probe.
- **Safety poll**: every 10 s (`Realtime:SafetyPollInterval`) while healthy, which covers lost
  notifications.
- **Failure** (socket error, `57P01`, failover): mark the listener down (metric), switch the poll to
  1 s (`Realtime:DegradedPollInterval`), and reconnect with exponential backoff from 1 s to 30 s
  with jitter. On reconnect, `LISTEN` and probe, then return to the 10 s poll. Reads use the shared
  pool and retry transient errors as `JobStoreDatabase` does. An RDS Multi-AZ failover therefore
  costs at most the reconnect time and loses nothing, because positions live in the database.
- **Shutdown** (SIGTERM): readiness reports not ready, and new realtime requests get 503 with
  `Retry-After: 2`. Open streams are closed over `Realtime:ShutdownDrain` (20 s, which is less than
  the target group's 30 s deregistration delay). Closes are spread evenly over that window. SSE
  streams get a final `retry: <1000–5000 ms, jittered>` and end. WebSockets close with code 1012
  (service restart). The listener closes last. Clients reconnect to another task with their last
  id.

## 7. Backpressure and slow consumers
- Every transport connection has a bounded channel (`MaxBufferPerConnection`, 256, which exists
  today for features and observations). The tailer never awaits a client: it uses `TryWrite`.
- A full channel follows the existing feature-stream rule. During replay drain the frame is dropped
  and later recovered by the dedup window. After the drain grace the session is closed with
  `slow-consumer`, after sending `reset(slow-consumer, head)` if there is room. The client resumes
  from its last id.
- A subscription that falls more than `Realtime:ResumeMaxEvents` behind during replay receives
  `reset(too-far-behind)` and must snapshot.
- A task whose dispatch lags the head by more than 30 s keeps reading in order and reports the lag
  (section 11). It does not skip.
- Admission limits stay per task (`MaxConcurrentSessions`, per-tenant and per-principal caps). With
  T tasks the install admits T times the cap. This is documented, as for rate limits.

## 8. Authorization
### 8.1 Per subscription, per event
- Admission runs the same policy evaluation as a read of the requested layers, datastreams, maps,
  jobs or admin topics (`StreamSubscriberSecurity`, `StudioSavedMapCollaborationAuthorizer`, the
  SensorThings tenant scope, and admin authorization). A subscription to a layer the principal
  cannot read is refused. A wildcard subscription is narrowed to the readable set.
- Every event is checked again at dispatch for each subscriber against that subscriber's current
  policy snapshot: layer visibility, row predicates over the committed or pre image, and the field
  mask projection (`StreamSubscriberSecurity.Allows` and `Project`). An anonymous principal can
  never be granted a secured layer, so a secured-layer event never reaches it, whether it came from
  the application or from SQL.
- Jobs: requester or admin. MCP: the session's principal. Admin topics: admin authorization.

### 8.2 Permission, catalog and credential changes
Statement triggers append to `security`:

| Table | Event kind | Subject |
|---|---|---|
| `rbac_roles`, `rbac_role_permissions`, `rbac_rls_policies`, `rbac_field_mask_policies`, `portal_item_shares` | `policy-changed` | `*` |
| `rbac_role_memberships`, `managed_user_roles` | `principal-changed` | the principal id |
| `admin_api_keys` (update of revocation or expiry, delete) | `credential-revoked` | key id |
| `security_tokens` delete where `expires_at > now()` (sign-out and revocation, not expiry), and insert of kind `portal-revocation` | `credential-revoked` | token hash, or principal id for revocation markers |

The `catalog` family (a change of `metadata_v2_current.etag`) counts as `policy-changed` for
authorization.

On these events every task does the following:
- `policy-changed`: refresh every subscription's policy snapshot (the existing
  `RefreshSubscriberSecurityAsync`, parallelism 8). Frames are filtered by the new snapshot from the
  next dispatched sequence. A subscription with nothing left readable closes with `access-revoked`.
- `principal-changed`: refresh that principal's subscriptions only.
- `credential-revoked`: close streams admitted with that credential (each stream records the
  credential kind and hash, or the key id, at admission) with `access-revoked`. Expiry needs no
  event: each stream closes itself at its credential's expiry.
- Backstop: a full re-evaluation every 30 s (`Realtime:SecurityBackstopInterval`). This replaces
  the 1 s `LiveStreamAuthorizationFilter.RevalidationInterval` re-check.

Ordering rule: a frame whose sequence was dispatched after the task processed the security event is
filtered by the new policy. The revocation latency target is the realtime target (p99 ≤ 1 s).

## 9. Endpoints and resume
All SSE responses send `Content-Type: text/event-stream`, `Cache-Control: no-store, no-transform`
and `X-Accel-Buffering: no`, and flush after every frame. A heartbeat (`: hb`) goes out every 15 s
(`Realtime:HeartbeatInterval`; the feature-stream default changes from 30 s). Each event has
`id: <family>:<seq>` and `event: <kind>`. A reset is
`event: reset` with data `{reason, head}`. WebSocket transports send a ping every 15 s.

| Route | Transport | Family | Resume input | Notes |
|---|---|---|---|---|
| `GET /api/v1/streaming/features` | SSE (default) and WebSocket (existing control frames) | `features` | `Last-Event-ID` or `cursor=features:<generation>` | A bare number gets `reset(cursor-epoch)`. The snapshot and resnapshot rules in `FeatureStreamEndpoints.Snapshot.cs` are unchanged |
| `GET /api/v1/admin/feature-events/replay` | JSON | `features` | `cursor` (generation) | Reads `PostgresFeatureChangeEventStore` |
| `GET /api/v1/saved-maps/{mapId}/collaboration/sessions/stream` | WebSocket | `collab-ops` and `collab-presence` | `resumeFrom=<server_cursor>` (existing). The first server frame carries the head and the presence snapshot | Out-of-window positions keep the existing `resync` frame |
| `GET /sta/v1.1/ObservationsStream` | SSE | `observations` | `Last-Event-ID` | The live-only refusal is removed |
| `GET /api/v1/admin/operations/streaming/alerts` | WebSocket (unchanged) | `alerts` | `cursor=alerts:<seq>` query | One-way; moving it to SSE is not in scope |
| `GET /api/v1/streaming/jobs?jobId=…` | SSE (new) | `jobs` | `Last-Event-ID` | Up to 50 job ids. Requester or admin |
| `GET /api/v1/streaming/layers?serviceId=…` | SSE (new) | `catalog` plus a catalog read | `Last-Event-ID` | Emits `layer.changed` per layer whose definition differs between etags, filtered by readability |
| `GET /api/v1/admin/live?topics=…` | SSE (new) | `admin` | `Last-Event-ID` | Replaces `/hubs/admin` |
| MCP stream (`McpEndpointExtensions`) | SSE (existing) | `mcp` | `Last-Event-ID` | Scope: the session id and `*` |

### 9.1 Resume algorithm
1. Parse the position. A wrong family prefix or an unparseable value → `400`. A bare number on
   `features` → `reset(cursor-epoch)`.
2. Read the window. If the position is older than the oldest retained position, or more than
   `ResumeMaxEvents` behind, send `reset(retention|too-far-behind, head)`. The feature and
   collaboration endpoints follow it with their snapshot.
3. Register the live subscription first, buffering into the bounded channel. Then replay
   `seq > position` from the table, in pages of `ReplayBatchSize`, through the same filter. Then
   drain the live buffer, skipping ids already sent. This is the order and dedup that
   `FeatureStreamEndpoints.Sse.cs` uses today.

### 9.2 Collaboration edits
Edits keep going through `SavedMapOperationAppendCoordinator` and
`PostgresSavedMapOperationLogRepository`. The head row is taken `FOR UPDATE`, the operation is
checked against `base_cursor`, and it is inserted with the next `server_cursor` in one transaction.
The `collab-ops` trigger appends in the same transaction. Two editors on different tasks therefore
conflict in PostgreSQL. The loser gets the existing conflict result, and both see the winner's
operation through the log. The per-task stripes in the coordinator remain a local optimisation.
`CanAcceptEdits` becomes `!topology.IsMultiReplica || (repository.SupportsReplicaSharedReplay &&
provider.Capabilities.CrossNode && provider.Capabilities.OrderedPerFamily)`.

### 9.3 Admin live view: SSE, not SignalR
Recommendation: move to SSE and delete the hub. Reasons:
1. The traffic is one-way. The only client-to-server calls are subscribe and unsubscribe for four
   groups, which become the `topics` query parameter.
2. SSE resumes from the log through `Last-Event-ID`. SignalR has no resume, so events sent while a
   client reconnects after task replacement are lost. That would fail acceptance test 3.
3. A cross-node `HubLifetimeManager` over the log would reimplement all-, group-, user- and
   connection-addressed sends, group membership and client results. That is a larger surface than
   the SSE endpoint, and it would exist only to serve four one-way groups.
4. SignalR's non-WebSocket transports (SSE and long polling) need sticky sessions, because the
   negotiate request and the transport requests must reach the same server. The target group has
   no stickiness, and CloudFront does not provide it.
5. There is one realtime stack: one heartbeat, backpressure, authorization and CDN behaviour set.
   The SignalR packages leave the image.

Cost: the Console's SignalR connection factory and three clients (`ConsoleAdminHubConnectionFactory`,
`SignalRConsoleOpsHealthRealtimeClient`, `SignalRConsoleDeployOperationRealtimeClient`,
`SignalRConsoleProposalRealtimeClient` in honua-console) become one streaming-fetch SSE client.
Console and Server change in the same release. The status frame (`event: status`) keeps
`AdminRealtimeStatus` without `backplaneEnabled`, and `realtimeGroups` becomes `topics`.

## 10. CloudFront, load balancer and ECS
These are changes to honua-io/honua-marketplace `aws/cloudformation/container-product/deploy.yaml`.
That repository is not edited here.

1. `Alb.LoadBalancerAttributes`: add `idle_timeout.timeout_seconds: '120'` (60 s today, by
   default). Heartbeats every 15 s keep connections active, and 120 s leaves margin for a delayed
   heartbeat.
2. `Targets.TargetGroupAttributes`: keep `deregistration_delay.timeout_seconds: '30'`, which is
   above the server's 20 s drain. Add `load_balancing.algorithm.type: least_outstanding_requests`,
   so that open streams, which count as outstanding requests, steer new connections to less loaded
   tasks.
3. `Distribution.CacheBehaviors`: add the following behaviours, all **before** `'*/admin'` and
   `'*/admin/*'`, with `TargetOriginId: honua`, `ViewerProtocolPolicy: redirect-to-https`,
   `AllowedMethods: [GET, HEAD, OPTIONS]`, `CachedMethods: [GET, HEAD]`, **`Compress: false`**,
   `CachePolicyId: 4135ea2d-6df8-44a3-9df3-4b5a84be39ad` (managed CachingDisabled),
   `OriginRequestPolicyId: 216adef6-5c7f-47e4-b989-5492eafa07d3` (managed AllViewer, which forwards
   `Last-Event-ID`, `Sec-WebSocket-*` and credentials), the `CacheBypassFunction` association and
   `ResponseHeadersPolicyId: ApiHeaders`:
   - `/api/v*/streaming/*`
   - `/api/v*/saved-maps/*/collaboration/sessions/stream*`
   - `/api/v*/admin/live*`
   - `/api/v*/admin/operations/streaming/*`
   - `/sta/v1.1/ObservationsStream*`
4. `/mcp*`: change `Compress` to `false`, because the MCP GET stream is SSE.
5. `/hubs/*`: remove it in the release that removes the hub.
6. Origin `OriginReadTimeout: 60` and `OriginKeepaliveTimeout: 55` stay. CloudFront's read timeout
   also bounds the gap between packets, and the 15 s heartbeat is well inside it.
7. ECS: `StopTimeout: 120` already exceeds the drain. There is no new environment variable, because
   `Realtime:Provider=auto` follows `Deployment__Mode=MultiNode`. If an RDS Proxy is ever added,
   set `ConnectionStrings__RealtimeListener` to the instance endpoint.

## 11. Metrics and alarms
Instruments are on the existing Honua meter. The feature-stream and observation-stream instruments
are folded into these, tagged by `family` and `transport`.

| Instrument | Type | Meaning |
|---|---|---|
| `honua.realtime.subscribers` | gauge | Open subscriptions per task, family and transport |
| `honua.realtime.log.head` / `honua.realtime.dispatch.position` | gauge | Head and dispatched position per family |
| `honua.realtime.dispatch.lag.events` / `.lag.seconds` | gauge | `head - dispatched`; age of the oldest undispatched row |
| `honua.realtime.delivery.latency` | histogram | Commit (`created_at` or `changed_at`) to frame written, per family |
| `honua.realtime.listener.connected` | gauge 0/1 | Listener health per task |
| `honua.realtime.listener.reconnects` | counter | |
| `honua.realtime.wakeups` | counter | Tag `source=notify|safety-poll|degraded-poll` |
| `honua.realtime.resets` | counter | Tag `reason` |
| `honua.realtime.slow_consumer.closed` / `.frames.dropped` | counter | |
| `honua.realtime.authz.frames_filtered` / `.streams_revoked` | counter | |
| `honua.realtime.notify_queue.usage` | gauge | `pg_notification_queue_usage()`, sampled each minute by the lease holder |

Recommended alarms for the operator guide and the template's dashboard:
- `dispatch.lag.seconds` p99 above 5 s for 5 minutes;
- `listener.connected` at 0 on any task for 2 minutes;
- `slow_consumer.closed` above 1 % of subscribers in 5 minutes;
- `notify_queue.usage` above 0.25;
- `subscribers` above 80 % of the per-task cap;
- `delivery.latency` p95 above 1 s for 10 minutes.

## 12. Acceptance tests
These are in `tests/dotnet/Honua.Server.Tests/Realtime/` unless named otherwise. Two-task tests run
two server hosts against one Testcontainers PostGIS with `Deployment:Mode=MultiNode`, the pattern
used by ADR-0082's `TwoNodeTests`.

1. `TwoTaskRealtimeTests.FeatureEdit_OnTaskA_ReachesSseSubscriberOnTaskB_WithinTarget`: 200 edits
   applied on A, with the subscriber on B. p95 ≤ 500 ms, every edit received once and in generation
   order. Variants: an edit by direct SQL; the listener disabled (each edit within 1.5 s).
2. `TwoTaskRealtimeTests.ListenerKilled_DeliveryContinuesAndNothingIsLost`: `pg_terminate_backend`
   on A's listener in the middle of a run. All events are delivered, and `listener.reconnects`
   increases.
3. `TwoTaskRealtimeTests.Resume_AfterTaskReplacement_LosesNothing`: a subscriber on A reads k events,
   and A is stopped both gracefully and abruptly. Edits continue on B. The client reconnects to B
   with `Last-Event-ID`. The union of what it received, deduplicated by id, equals the committed
   set, in order. Repeated for observations, collaboration (`resumeFrom`) and the admin live view.
4. `TwoTaskRealtimeTests.SecuredLayer_NeverReachesAnonymousSubscriber`: an anonymous wildcard
   subscription on B while the application and SQL write to a secured layer on A. Zero frames.
   Variants: a row-policy predicate and a field mask, where only permitted rows and projected
   fields arrive.
5. `TwoTaskRealtimeTests.PermissionRevocation_OnTaskA_FiltersOrClosesStreamOnTaskB`: revoke the
   role on A. B delivers no frame with a sequence after the revocation commit, and the stream closes
   with `access-revoked` within 1 s. Credential variant: sign out on A, and B's stream closes within
   1 s.
6. `TwoTaskCollaborationTests.ConcurrentEdits_OnTwoTasks_OneConflictsAndBothSeeTheWinner`, plus
   presence that crosses tasks within 750 ms.
7. `RealtimeBackpressureTests.SlowConsumer_IsClosedAndResumesWithoutLoss`: memory stays bounded.
8. `RealtimeEventLogTests`: concurrent appends produce contiguous sequences in commit order; the
   sweep deletes only expired rows; a resume older than retention gets `reset(retention)`.
9. Scale (`Category=Scale`): 2 tasks, 2,000 subscribers and 50 edits per second for 10 minutes.
   p95 ≤ 1 s, one listener connection per task, and database CPU added ≤ 10 points against a run
   with no subscribers.
10. Live, on the design-partner stack and not in CI: SSE through CloudFront survives 10 minutes idle
    on heartbeats; a WebSocket upgrade succeeds through CloudFront; and a rolling deployment
    replaces every task while a client stays subscribed and loses nothing.

## 13. Work breakdown
Epic honua-io/honua-server#7339, release `2026.1-rc.3`. Order: #7340 and #7341 first; then #7342;
then #7343; then #7344, #7345, #7346 and #7347 in parallel, with #7348 alongside; #7349 last.

| Issue | Scope | Sections |
|---|---|---|
| #7340 | Event log tables, append function, retention | 3, 5 |
| #7341 | Fan-out provider interface, in-process and PostgreSQL providers, listener | 4, 6 |
| #7342 | Feature streams from `feature_changes`; delete the in-memory store and retry queue | 2, 9 |
| #7343 | Per-subscriber authorization and event-driven revocation | 8 |
| #7344 | Map collaboration across tasks | 5.4, 9.2 |
| #7345 | SensorThings observation stream with resume | 3, 9 |
| #7346 | Admin live view over SSE; remove the SignalR hub | 9.3 |
| #7347 | Alerts, MCP notifications, job and layer feeds | 3, 9 |
| #7348 | Drain, heartbeats, backpressure, metrics | 6.3, 7, 11 |
| #7349 | Two-task acceptance, scale run, live CDN check | 10, 12 |
