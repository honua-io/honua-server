---
type: guide
title: "Work with time"
description: "Make a layer time-aware, discover its temporal extent, and filter or animate it with the same time-window semantics across GeoServices, OGC, and vector tiles."
resource: "honua://capability/temporal.filtering"
resources:
  - "honua://capability/temporal.extent-discovery"
  - "honua://capability/temporal.histogram"
  - "honua://capability/temporal.time-series-tiles"
  - "honua://capability/temporal.animation-api"
---
# Work with time

Make a layer time-aware, discover its temporal extent, and filter or animate it with the same time-window semantics across GeoServices, OGC, and vector tiles.

**Prerequisites:** a running server ([quickstart](../../get-started/quickstart.md)), a published layer with `Date`/`DateTime` columns ([publish layers](../publish/publish-layers.md)), and an admin API key ([authentication](../secure/authentication.md)). Step 1 always needs the key; the quickstart also requires it for the reads in steps 2 to 5, so send it as `X-API-Key` (or use the authorized API explorer) instead of opening those URLs anonymously (an anonymous GeoServices read returns error code 499, "Authentication is required").

Temporal behavior is opt-in: a layer advertises a time dimension only after you name its start (and optionally end) time fields in catalog metadata. Accepted time values everywhere include ISO 8601 instants (`2024-06-15T12:00:00Z`), intervals (`start/end` for OGC, `start,end` for GeoServices/MVT), Unix epoch milliseconds, and open-ended bounds: `../end` for OGC `datetime`, `null,end` for GeoServices and MVT `time` (`time=../end` is rejected as a path traversal attempt).

## Steps

1. Mark the layer time-aware by naming its time fields. The fields must resolve to `Date`/`DateTime` columns on the layer:

   In the authorized [API explorer](../../reference/openapi-and-explorer.md), run `PUT /api/v1/admin/services/{service}/layers/{layerId}/metadata` with this body. `{layerId}` is the layer's FeatureServer id (the `id` listed by `/rest/services/{service}/FeatureServer?f=json`, named `serviceLayerIndex` in the API reference), which is not always 0; an id the service does not publish returns "Layer … not found":

   ```json
   {
     "timeInfo": {
       "startTimeField": "event_start",
       "endTimeField": "event_end"
     }
   }
   ```

2. Discover the temporal contract. Layer metadata now carries a GeoServices REST-compatible `timeInfo` block, and a dedicated endpoint returns the actual data extent:

> Request `/rest/services/{service}/FeatureServer/{layerId}?f=json` and `/rest/services/{service}/FeatureServer/{layerId}/temporalExtent?f=json` with your API key.

   `temporalExtent` returns `min`/`max` as ISO 8601 plus `minEpochMs`/`maxEpochMs`; for layers that are not time-aware it returns an error with code 404.

3. Filter queries by time. GeoServices takes `time=start,end`; OGC API Features takes `datetime=start/end`:

> Request `/rest/services/{service}/FeatureServer/{layerId}/query?where=1%3D1&time=2024-01-01T00:00:00Z,2024-12-31T23:59:59Z&f=json` and `/ogc/features/collections/my_layer/items?datetime=2024-01-01T00:00:00Z/2024-12-31T23:59:59Z` with your API key.

   A bare instant filters to that moment; bounds are inclusive; reversed ranges are rejected with code 400. For an open start, use `time=null,2024-02-01T00:00:00Z` or `datetime=../2024-02-01T00:00:00Z`.

4. Render time slices over WMS with the standard `TIME` parameter. GetCapabilities advertises `<Dimension name="time">` with the layer's live extent:

> Request `/rest/services/{service}/MapServer/WMS?SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0&LAYERS={layerId}&STYLES=&CRS=EPSG:4326&BBOX=-90,-180,90,180&WIDTH=512&HEIGHT=512&FORMAT=image/png&TIME=2024-06-15T00:00:00Z/2024-06-15T23:59:59Z` with your API key.

   WMTS GetTile accepts `time=` the same way, including the `default`/`current` tokens that resolve to the layer's maximum timestamp.

5. Animate vector tiles by requesting time-windowed MVT frames. Each distinct `?time=` range is cached as its own tile variant, so stepping a slider through windows stays fast:

> Request `/tiles/{layerId}/8/40/96.mvt?time=2024-01-01T00:00:00Z,2024-06-30T23:59:59Z` and `/tiles/{layerId}/8/40/96.mvt?time=2024-07-01T00:00:00Z,2024-12-31T23:59:59Z` with your API key.

   MVT `?time=` filtering requires the Pro `temporal.time-series-tiles` entitlement (402 without it). For planning animation frames, the Pro `queryDateBins` endpoint returns a time histogram (`GET .../FeatureServer/{layerId}/queryDateBins?binField=event_start&bin={"calendarBin":{"unit":"month"}}&f=json`).

## Verify

> Request `/rest/services/{service}/FeatureServer/{layerId}/temporalExtent?f=json` with your API key.

Expected (trimmed):

```json
{ "layerId": 0, "startTimeField": "event_start", "endTimeField": "event_end",
  "min": "2022-01-01T00:00:00.000Z", "max": "2024-12-31T23:59:59.000Z" }
```

## Troubleshoot

GeoServices REST endpoints (`/rest/services/...`) report errors as HTTP 200 with an `error` object whose `code` carries the status; OGC API Features returns the status itself.

- **Code 404 from `temporalExtent`** — the layer is not time-aware (no `timeInfo` configured, or the configured field does not resolve to a `Date`/`DateTime` column), or `{layerId}` is not a FeatureServer layer id of that service ("Service layer was not found").
- **Code 499 "Authentication is required" (GeoServices), or an HTTP error status (OGC, WMS, tiles)** — the request had no API key; the quickstart requires one for these reads.
- **Code 400 on `query?time=`** — same cause as above, a reversed range (`start > end`), or an open bound written with `..` (`../end` is reported as "Path traversal attempt detected"); write `null,end` instead.
- **WMS `GetMap` with `TIME` returns the whole layer** — the layer does not advertise a time dimension, so WMS 1.3.0 ignores `TIME` (Annex C.3.5) instead of returning `InvalidDimensionValue`; configure `timeInfo` first.
- **402 on `?time=` tiles or `queryDateBins`** — these need the Pro `temporal.time-series-tiles` / `temporal.histogram` entitlements.
- **501 from `PUT /api/v1/admin/services/{serviceName}/timeinfo`** — the service-level endpoint is intentionally not supported; use the per-layer metadata endpoint shown in step 1. See [troubleshooting](../deploy/troubleshooting.md).

## Next steps

- [Query features](query-features.md)
- [Style map layers](../style/style-maps.md)
- [React to changes](../edit/react-to-changes.md)
