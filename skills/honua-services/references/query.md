# Verify the requested result

Use the [query-view concept](https://honua.io/docs/concepts/domain/query-view/) and
[filter reference](https://honua.io/docs/reference/cql2-and-filtering/) for filter semantics.

Use `honua_list_layers` and `honua_describe_layer` to obtain the published layer
identity and field schema. Use the discovered `honua_query_features` schema for
the filter, selected fields, ordering, output CRS and paging arguments.

Verify the filter itself: string versus numeric comparisons, null behavior,
spatial reference and geometry extent can change the answer. Query the intended
published layer with that filter, not the unfiltered source table. For a count,
use the advertised count/statistics operation or page all required results;
a single page's length is not the total count. Respect continuation/transfer
limit indicators and keep a stable order when paging.

Record the query's layer id, filter and bounded verified result in the response.
If the requested count cannot be checked, say so. Do not turn an incomplete page
or an estimate into an exact claim.

For a timeout after publication, first read the catalog and operation state.
If the source exists but no completed publication does, diagnose and continue
from that source. Do not re-import or re-publish a committed service blindly.
