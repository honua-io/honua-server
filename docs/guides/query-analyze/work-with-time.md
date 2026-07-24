# Work with time

Make a layer time-aware, discover its temporal extent, and filter or animate it with the same time-window semantics across GeoServices, OGC, and vector tiles.

**Prerequisites:** a running server ([quickstart](../../get-started/quickstart.md)), a published layer with `Date`/`DateTime` columns ([publish layers](../publish/publish-layers.md)), and an admin API key for step 1 ([authentication](../secure/authentication.md)).

Temporal behavior is opt-in: a layer advertises a time dimension only after you name its start (and optionally end) time fields in catalog metadata. Accepted time values everywhere include ISO 8601 instants (`2024-06-15T12:00:00Z`), intervals (`start/end` for OGC, `start,end` for GeoServices/MVT), Unix epoch milliseconds, and open-ended bounds (`../end`, `null,end`).

## Steps

1. Mark the layer time-aware by naming its time fields. The fields must resolve to `Date`/`DateTime` columns on the layer:

   In the authorized [API explorer](../../reference/openapi-and-explorer.md), run `PUT /api/v1/admin/services/{service}/layers/{layerId}/metadata` with this body:

   ```json
   {
     "timeInfo": {
       "startTimeField": "event_start",
       "endTimeField": "event_end"
     }
   }
   ```

2. Discover the temporal contract. Layer metadata now carries an ArcGIS-compatible `timeInfo` block, and a dedicated endpoint returns the actual data extent:

> Open `/rest/services/{service}/FeatureServer/{layerId}?f=json`, `/rest/services/{service}/FeatureServer/{layerId}/temporalExtent?f=json` in a browser.

   `temporalExtent` returns `min`/`max` as ISO 8601 plus `minEpochMs`/`maxEpochMs`; it is 404 for layers that are not time-aware.

3. Filter queries by time. GeoServices takes `time=start,end`; OGC API Features takes `datetime=start/end`:

> Open `/rest/services/{service}/FeatureServer/{layerId}/query?where=1%3D1&time=2024-01-01T00:00:00Z,2024-12-31T23:59:59Z&f=json`, `/ogc/features/collections/my_layer/items?datetime=2024-01-01T00:00:00Z/2024-12-31T23:59:59Z` in a browser.

   A bare instant filters to that moment; bounds are inclusive; reversed ranges are rejected with 400.

4. Render time slices over WMS with the standard `TIME` parameter. GetCapabilities advertises `<Dimension name="time">` with the layer's live extent:

> Open `/rest/services/{service}/MapServer/WMS?SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0&LAYERS={layerId}&STYLES=&CRS=EPSG:4326&BBOX=-90,-180,90,180&WIDTH=512&HEIGHT=512&FORMAT=image/png&TIME=2024-06-15T00:00:00Z/2024-06-15T23:59:59Z` in a browser.

   WMTS GetTile accepts `time=` the same way, including the `default`/`current` tokens that resolve to the layer's maximum timestamp.

5. Animate vector tiles by requesting time-windowed MVT frames. Each distinct `?time=` range is cached as its own tile variant, so stepping a slider through windows stays fast:

> Open `/tiles/{layerId}/8/40/96.mvt?time=2024-01-01T00:00:00Z,2024-06-30T23:59:59Z`, `/tiles/{layerId}/8/40/96.mvt?time=2024-07-01T00:00:00Z,2024-12-31T23:59:59Z` in a browser.

   MVT `?time=` filtering requires the Pro `temporal.time-series-tiles` entitlement (402 without it). For planning animation frames, the Pro `queryDateBins` endpoint returns a time histogram (`GET .../FeatureServer/{layerId}/queryDateBins?binField=event_start&bin={"calendarBin":{"unit":"month"}}&f=json`).

## Verify

> Open `/rest/services/{service}/FeatureServer/{layerId}/temporalExtent?f=json` in a browser.

Expected (trimmed):

```json
{ "layerId": 0, "startTimeField": "event_start", "endTimeField": "event_end",
  "min": "2022-01-01T00:00:00.000Z", "max": "2024-12-31T23:59:59.000Z" }
```

## Troubleshoot

- **404 from `temporalExtent`** — the layer is not time-aware: no `timeInfo` configured, or the configured field does not resolve to a `Date`/`DateTime` column.
- **400 on `query?time=`** — same cause as above, or a reversed range (`start > end`).
- **WMS `InvalidDimensionValue` / WMTS `InvalidParameterValue`** — `TIME` was sent to a layer that does not advertise a time dimension; configure `timeInfo` first.
- **402 on `?time=` tiles or `queryDateBins`** — these need the Pro `temporal.time-series-tiles` / `temporal.histogram` entitlements.
- **501 from `PUT /api/v1/admin/services/{serviceName}/timeinfo`** — the service-level endpoint is intentionally not supported; use the per-layer metadata endpoint shown in step 1. See [troubleshooting](../deploy/troubleshooting.md).

## Next steps

- [Query features](query-features.md)
- [Style map layers](../style/style-maps.md)
- [React to changes](../edit/react-to-changes.md)
