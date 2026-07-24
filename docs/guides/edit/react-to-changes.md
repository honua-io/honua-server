# React to feature changes

Receive an event for every feature insert, update, and delete — pushed to your endpoint as a signed webhook or streamed live over WebSocket/SSE.

**Prerequisites:** A writable layer (see [Edit features](edit-features.md)). Streaming requires a Pro or Enterprise license — Community returns `403` and advertises `enabled=false`. Webhook configuration requires restart access to the server's environment; replay and session admin require an admin API key.

Every write — FeatureServer, OGC API Features, OData (including `$batch`), WFS 2.0 transactions, and gRPC `ApplyEdits` — publishes one normalized envelope: `eventId`, `timestamp`, `serviceId`, `layerId`, `objectId`, `operation` (`insert`|`update`|`delete`), `protocol`, `requestId`, plus geometry/attributes when available. Delivery is at-least-once on every channel; always dedupe on `eventId`.

## Steps

### 1. Configure a webhook destination

Webhooks are configured through server settings (environment variables), not the admin API:

```bash
FeatureChangeEvents__Webhook__Enabled=true
FeatureChangeEvents__Webhook__Url=https://hooks.example.com/honua
FeatureChangeEvents__Webhook__Secret=$WEBHOOK_HMAC_SECRET
FeatureChangeEvents__Webhook__MaxAttempts=5
```

The URL must be HTTPS, carry no embedded credentials, and resolve to a public address — private/loopback targets are rejected at startup. Retries use exponential backoff (`InitialBackoffMs`/`MaxBackoffMs`/`RequestTimeoutSeconds` tune it).

### 2. Verify webhook signatures in your receiver

Each delivery carries `X-Honua-Event-Id`, `X-Honua-Event-Timestamp` (unix seconds), `Idempotency-Key` (= the event id), and `X-Honua-Signature: sha256=<hex>` — an HMAC-SHA256 of `<timestamp>.<raw-json-body>` using your configured secret:

```bash
printf '%s.%s' "$TIMESTAMP" "$RAW_BODY" | openssl dgst -sha256 -hmac "$WEBHOOK_HMAC_SECRET"
```

### 3. Stream over SSE

> Use `createHonuaServerRealtimeSubscription` from `@honua/sdk-js/realtime` and subscribe with `layerId: 0`; the SDK handles the SSE endpoint and event decoding.

SSE subscriptions are fixed by query parameters (`serviceId`, `layers`, `bbox`, `filter` CQL2, `datetime`) at connect time; each `feature-change` event's SSE `id:` is a cursor, so `EventSource` reconnects resume automatically (or pass `cursor=` explicitly).

### 4. Stream over WebSocket with dynamic subscriptions

Connect a WebSocket to `GET /api/v1/streaming/features`, then send JSON control frames:

```json
{"type":"subscribe","subscriptionId":"alpha","layerId":0,"bbox":[-158,21,-157,22],"filter":"status = 'active'","cursor":1234}
```

One socket carries many subscriptions; the server answers with `status`, `heartbeat`, `error`, and `feature-change` frames, and `cursor` triggers replay of missed events before live delivery. Unfiltered all-layer streams require admin access; everyone else subscribes to explicit service or layer scopes.

### 5. Replay missed events (recovery)

> Use the [API explorer](../../reference/openapi-and-explorer.md) for `GET /api/v1/admin/feature-events/replay?limit=100`.

Page with `cursor` (and optional `from`/`to` ISO 8601 bounds, `limit` 1–1000): process each event idempotently, persist the returned `nextCursor`, repeat while `hasMore=true`. Retention is in-memory and capped by `FeatureChangeEvents__MaxRetainedEvents` (default 20000); on PostgreSQL a transactional outbox makes publication atomic with the row mutation.

### 6. Manage live sessions (admin)

> Use the [API explorer](../../reference/openapi-and-explorer.md) for `GET /api/v1/admin/streaming/features/sessions`.

`DELETE /api/v1/admin/streaming/features/sessions/{sessionId}` force-disconnects one.

## Verify

> Open `/api/v1/streaming/features/capabilities` in a browser.

```json
{ "enabled": true, "transports": ["websocket", "sse"], "replay": { "supported": true }, "layers": [ { "layerId": 0, "canSubscribe": true } ] }
```

Then insert a feature in another terminal ([Edit features](edit-features.md)) and watch the `feature-change` event arrive on your open SSE stream.

## Troubleshoot

| Symptom | Fix |
|---|---|
| `403` before the stream connects | Community edition fails closed; streaming needs Pro or Enterprise. Capabilities reports `enabled=false`. |
| `401` on an unfiltered stream | All-layer streams require admin. Subscribe with explicit `layers=` / `serviceId=` scopes instead. |
| `503` on connect | `FeatureStreaming__MaxConcurrentSessions` is exhausted; raise it or free sessions via the admin sessions endpoint. |
| Webhook never fires | The URL must be HTTPS and publicly resolvable — private, loopback, or unresolvable addresses are rejected. Check startup logs and your receiver's signature validation. |
| Duplicate events | Expected: delivery is at-least-once (and once per matching subscription). Dedupe on `eventId` (or `(subscriptionId, eventId)` for multi-subscription sockets). |

More general failures: [Troubleshooting](../deploy/troubleshooting.md).

## Next steps

- [Edit features](edit-features.md)
- [Monitoring](../deploy/monitoring.md)
