# Feature Streaming

Honua exposes Pro-tier feature-change streams for SDKs and applications that need live insert, update, and delete events. Streams are volume reducers for client workflows; SDK-local geofence definitions and geofence evaluation remain client-side responsibilities.

## Endpoints

- WebSocket: `GET /api/v1/streaming/features` with a WebSocket upgrade.
- SSE: `GET /api/v1/streaming/features` with `Accept: text/event-stream`.
- Capabilities: `GET /api/v1/streaming/features/capabilities`.

Community edition fails closed. The stream endpoint returns `403` before accepting a connection, and capabilities return `enabled=false` with no transports. Pro and Enterprise advertise `websocket` and `sse`.

Unfiltered all-layer streams require admin access. Non-admin WebSocket clients can connect, receive a status frame, and then subscribe to explicit service or layer scopes. SSE subscriptions are fixed by query parameters and end when the client closes the connection.

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

SSE event names are `status`, `heartbeat`, and `feature-change`. Reconnect uses `Last-Event-ID` first, then the `cursor` query parameter.

## Event Envelope

`feature-change` frames use one shared envelope across WebSocket, SSE, replay, and webhooks where available:

```json
{
  "type": "feature-change",
  "eventId": "evt",
  "sourceId": "rest",
  "serviceId": "parks",
  "layerId": 0,
  "featureId": "42",
  "objectId": 42,
  "operation": "insert",
  "timestamp": "2026-05-01T00:00:00Z",
  "cursor": 1234,
  "geometry": { "type": "Point", "coordinates": [-157.8583, 21.3069] },
  "geometryCrs": "EPSG:4326",
  "attributes": { "name": "Honolulu" }
}
```

`operation` is one of `insert`, `update`, or `delete`. Delete geometry and attributes are emitted only when the mutation source provides a before-image.

## Filters

- `serviceId`: limits events to a published service.
- `layers` or `layerIds`: comma-separated layer ids.
- `bbox`: `minX,minY,maxX,maxY`; accepted in EPSG:4326 by default, or with `bboxCrs=EPSG:4326`.
- `filter`: CQL2 text or JSON, selected by `filter-lang`. Streaming accepts the in-memory scalar subset only.
- `datetime`: OGC datetime instant or interval. Accepted only for layers with `timeInfo`.

Unsupported polygon intersects filters, invalid CRS values, non-spatial bbox targets, unknown filter fields, excessive filter depth, functions, and temporal filters on non-time-aware layers return client-safe `400` errors.

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

Capabilities report the active heartbeat interval, cursor retention limit, max sessions, transports, filter families, and per-layer spatial/temporal support.
