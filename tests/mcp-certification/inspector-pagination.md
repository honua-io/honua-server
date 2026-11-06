# Public Inspector pagination observation

This server-owned helper forwards the installed Inspector's HTTP requests and
candidate responses without creating or editing MCP methods, IDs, cursors,
sessions or resources. The canonical producer invokes the public Inspector CLI
against its loopback observer endpoint; the observer forwards only to the exact
configured `/mcp` endpoint. It rejects redirects and never records credentials.

Contract `honua.inspector-pagination-observer/v2` observes `tools/list`, not
`resources/list`. The candidate's real `resources/list` has 8 static resources and
the default list page size is 12 (`McpPagination.DefaultListPageSize`), so it never
produces a second page. The authenticated explicit full catalog export
(`_meta["honua.io/workflow-view"]="full"`, about 132 tools) spans about 11 real
default pages. Server page sizes are not changed to manufacture pages. The
canonical producer runs the public CLI with
`--method tools/list --metadata 'honua.io/workflow-view="full"'` and its API key
header; the Inspector's managed refresh follows every `nextCursor` itself.

A proof requires at least two actual `tools/list` pages, matching response/request
IDs, a stable session context, every page selecting the full view through `_meta`
with no overriding `params.view`, a credential header (`X-API-Key` or
`Authorization`) on every page, every opaque continuation cursor handed to the
next request exactly once, a final page without continuation, unique tool names,
and the Inspector's final tool list matching the entire observed chain in order.
A single-page result, the bounded default view, or an anonymous request is not a
cursor-traversal proof. Only credential presence is checked; its value is never
retained. Captured tool names and session IDs are hashed in retained proof. Error text is fixed and contains no
upstream body. Requests/pages, bytes and observation time are bounded; all owned
connections close on every exit. Compressed bytes and headers reach the client
unchanged. The observer independently decodes gzip, deflate and Brotli captures
with a 4 MiB decoded page limit and 16 MiB decoded total limit; unknown, corrupt
or oversized encodings cannot produce proof.

The controls run the installed public CLI from the canonical producer's
immutable dependency lock at `372d9ad1366a5fa28165e7ebbc7481e6e1ca13c4`.
That lock installs Inspector **2.10.1**. The separate release requirements still
name **2.3.0**; these controls do not qualify that version, change its requirement,
or resolve the existing release181 catalog mismatch. Scripted peer successes
and negative controls are explicitly labelled `candidateQualified: false`.
The actual candidate must supply the pages; controls never become candidate
receipts. Missing or mismatched immutable helper bytes remain nonpassing.

This implements pagination observation only. Inspector ping addressability,
unknown-tool errors and cancellation proof compatibility remain open under the
canonical producer's existing work. A new server image alone cannot clear them.
