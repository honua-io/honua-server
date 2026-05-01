# Feature Streaming

Honua exposes Pro-tier feature-change streams for SDKs and applications that need live insert, update, and delete events. Streams are volume reducers for client workflows; SDK-local geofence definitions and geofence evaluation remain client-side responsibilities.

## Endpoints

- WebSocket: `GET /api/v1/streaming/features` with a WebSocket upgrade.
- SSE: `GET /api/v1/streaming/features` with `Accept: text/event-stream`.
- Capabilities: `GET /api/v1/streaming/features/capabilities`.
- Admin sessions: `GET /api/v1/admin/streaming/features/sessions` lists active sessions; `DELETE /api/v1/admin/streaming/features/sessions/{sessionId}` force-disconnects one. These admin routes require admin authorization.

Community edition fails closed. The stream endpoint returns `403` before accepting a connection, and capabilities return `enabled=false` with no transports. Pro and Enterprise advertise `websocket` and `sse`.

Unfiltered all-layer streams require admin access. Non-admin WebSocket clients can connect, receive a status frame, and then subscribe to explicit service or layer scopes. SSE subscriptions are fixed by query parameters at connect time and end when the client closes the connection.

When the configured `MaxConcurrentSessions` limit is reached, new connections are rejected with `503 Service Unavailable` (RFC 7807 problem) before the socket is upgraded.

## WebSocket Frames

Client control frames are JSON:

```json
{"type":"subscribe","layerId":0,"bbox":[-158,21,-157,22],"filter":"status = 'active'","datetime":"2026-01-01T00:00:00Z/.."}
{"type":"unsubscribe","subscriptionId":"<id-from-status>"}
{"type":"ping"}
```

The server sends:

- `status` frames for `connected`, `subscribed`, `unsubscribed`, and ping `ok`.
- `error` frames for invalid control messages.
- `heartbeat` frames on the configured heartbeat interval.
- `feature-change` frames for matching events.

Each data frame includes `subscriptionId` so one WebSocket can carry multiple subscriptions.

## SSE Events

SSE clients pass the initial subscription in the query string:

```text
GET /api/v1/streaming/features?layers=0&bbox=-158,21,-157,22&filter=status%20%3D%20%27active%27
Accept: text/event-stream
```

SSE event names are `status`, `heartbeat`, and `feature-change`. Each `feature-change` event is emitted with an SSE `id:` set to the cursor so the browser EventSource can resume automatically. Reconnect uses the `cursor` query parameter when present, and otherwise falls back to the `Last-Event-ID` request header.

## Event Envelope

`feature-change` frames use one shared envelope across WebSocket, SSE, replay, and webhooks where available:

```json
{
  "type": "feature-change",
  "eventId": "evt",
  "cursor": 1234,
  "timestamp": "2026-05-01T00:00:00Z",
  "sourceId": "rest",
  "serviceId": "parks",
  "layerId": 0,
  "featureId": "42",
  "objectId": 42,
  "operation": "insert",
  "protocol": "OGC-Features",
  "requestId": "01HXYZ...",
  "subscriptionId": "default",
  "geometry": { "type": "Point", "coordinates": [-157.8583, 21.3069] },
  "geometryCrs": "EPSG:4326",
  "attributes": { "name": "Honolulu" },
  "geometryChanged": true
}
```

`operation` is one of `insert`, `update`, or `delete`. `protocol` identifies the originating mutation surface (`FeatureServer`, `OGC-Features`, `OData`, or `Grpc`); `requestId` echoes the originating request for correlation; `subscriptionId` echoes the subscription that matched. Delete geometry and attributes are emitted only when the mutation source provides a before-image. `changedAttributes` and `geometryChanged` are best-effort delta hints when the source protocol can supply them.

## Filters

- `serviceId`: limits events to a published service. The caller must have read access to every layer in that service.
- `layers` (preferred) or `layerIds` (legacy alias): comma-separated layer ids. Each layer is access-checked.
- `bbox`: `minX,minY,maxX,maxY`. Accepted in EPSG:4326 by default; an explicit CRS via `bboxCrs` (or kebab-case `bbox-crs`) must resolve to EPSG:4326. The server projects the bbox into the layer SRID once at subscription setup. `bbox` requires exactly one layer.
- `filter`: CQL2 attribute filter selected by `filter-lang` (`cql2-text` default, `cql2-json` alternative). Only the in-memory scalar subset is accepted: scalar comparisons, boolean combinations, null checks, `IN`/`NOT IN`, and simple `LIKE`. Functions, spatial predicates, temporal predicates, and unknown fields are rejected. The expression is depth-limited; over-deep expressions are rejected with the depth in the error body. `filter` requires exactly one layer.
- `datetime` (alias `time`): OGC datetime instant or interval. Accepted only for layers with `timeInfo`; evaluated against the layer's start/end time fields. `datetime` requires exactly one time-aware layer.

Polygon intersects (`polygon` / `intersects` query parameters) are explicitly rejected with `polygonIntersects stream filters are not supported by the active feature-change event source.` Unsupported CRS values, non-spatial bbox targets, unknown filter fields, excessive filter depth, functions, and temporal filters on non-time-aware layers all return client-safe `400` errors.

WebSocket `subscribe` control frames accept the same filter shape (`serviceId`, `layerId`/`layers`/`layerIds`, `bbox`, `bboxCrs`, `filter`, `filterLang`, `datetime`) plus an optional `cursor` for per-subscription replay. The same single-layer constraints apply.

## Replay And Backpressure

Clients should persist the highest delivered `cursor` per subscription. On reconnect, pass that cursor to receive events after it. Delivery is at least once, so consumers should de-duplicate by `eventId` and ignore stale cursors already processed.

Each connection has a bounded outbound queue. Slow consumers are disconnected after the replay handoff grace window is exhausted. Heartbeat events let clients detect idle-but-healthy connections.

## Configuration

Relevant settings:

- `FeatureStreaming:HeartbeatInterval`
- `FeatureStreaming:MaxBufferPerConnection`
- `FeatureStreaming:MaxConcurrentSessions`
- `FeatureStreaming:ReplayBatchSize`
- `FeatureStreaming:CrossNodeSyncInterval`
- `FeatureChangeEvents:MaxRetainedEvents`

The capabilities response reports the active edition, minimum required edition, transports, filter families, replay support, cursor retention limit, heartbeat interval, max concurrent sessions, delete before-image availability, and a per-layer summary covering `canSubscribe`, `supportsSpatialFilters`, `supportsTemporalFilters`, layer time fields, and layer CRS. Capability values vary by edition and per-caller layer access.
