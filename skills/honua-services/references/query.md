# Verify a feature query

`honua_query_features` requires `serviceId` and integer `serviceLayerIndex` from the same `honua_list_layers` row. The index starts at zero and identifies a layer within that service. Keep the pair together when selecting a layer; a storage layer ID from admin discovery is a different identifier.

Discover the layer before querying. If `honua_list_layers` reports `hasMore: true`, repeat discovery with `offset` set to `nextOffset` until the selected layer is found or `hasMore` is false. Use the selected pair with `honua_describe_layer` to inspect fields before choosing a filter.

`where` is an SQL WHERE clause, for example `population > 10000`. It is not a CQL2 filter. Use field names and types from `honua_describe_layer`. Quote string literals. Compare numbers without quotes.

`returnCountOnly` true returns `count` and no features. One page length is not a count. `limit` defaults to 100 and caps at 1000. When `exceededTransferLimit` is true, send the same query again with `cursor` set to `nextCursor` until it is false.

For example, if discovery returned `serviceId: "city-data"` and `serviceLayerIndex: 0`, these are arguments for a count of all visible features:

```json
{
  "serviceId": "city-data",
  "serviceLayerIndex": 0,
  "returnCountOnly": true
}
```

Use the discovered values in place of this illustrative pair. Read `count` from the response; do not infer it from a feature page. To inspect the first page for the same layer, call `honua_query_features` with:

```json
{
  "serviceId": "city-data",
  "serviceLayerIndex": 0,
  "limit": 100
}
```

If that page reports `exceededTransferLimit: true`, repeat these arguments with `cursor` set to the returned `nextCursor`. Preserve any `where`, spatial filter and field selection across pages. Stop when `exceededTransferLimit` is false. A refused call or incomplete pagination does not establish a complete result.

Record the service ID, service-local index, where clause, and the count or features you actually received. Include whether pagination completed so the result can be checked against the same layer and filter.
