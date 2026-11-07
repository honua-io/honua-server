# Verify a feature query

`honua_query_features` requires `serviceId` and integer `layerId` from `honua_list_layers`.

`where` is an SQL WHERE clause, for example `population > 10000`. It is not a CQL2 filter. Use field names and types from `honua_describe_layer`. Quote string literals. Compare numbers without quotes.

`returnCountOnly` true returns `count` and no features. One page length is not a count. `limit` defaults to 100 and caps at 1000. When `exceededTransferLimit` is true, send the same query again with `cursor` set to `nextCursor` until it is false.

Record the service id, layer id, where clause, and the count or features you actually received.
