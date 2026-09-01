# Drive Studio from Claude Desktop

Claude Desktop can act as an external MCP host for the same draft tools. This
is a **preview integration** in 2026.1, not a promise of GA support for either
browser Studio or a particular Claude Desktop configuration format.

Point an HTTP-capable MCP connector at:

```text
https://your-honua.example.com/mcp
```

The connector must send a bearer token for a user authorized to the target
tenant and Studio resources. Discover tools with `tools/list`; do not hard-code
the 17-name table as an authorization boundary. A safe turn is:

1. call `honua_studio_create_draft`;
2. apply typed mutations using the returned `generation`;
3. on `failed_precondition`, fetch, reconcile, and retry once;
4. validate and preview the draft;
5. save/reopen through an SDK client if durable versioning is required.

`honua_studio_propose_publication` records intent, but do not promise a public
URL: the governed publication journey is blocked by
[#3304](https://github.com/honua-io/honua-server/issues/3304).
