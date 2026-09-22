---
type: guide
title: "Drive Studio from Claude Desktop"
description: "Claude Desktop can act as an external MCP host for the same draft tools."
resource: "honua://capability/ai.mcp-discovery"
---
# Drive Studio from Claude Desktop

Claude Desktop can act as an external MCP host for the same draft tools. This
is a **preview integration** in 2026.1, not a promise of GA support for either
browser Studio or a particular Claude Desktop configuration format.

## Connect Claude Desktop

Claude Desktop launches MCP servers over stdio, and a Honua deployment speaks
Streamable HTTP at `POST /mcp`. `honua-mcp-proxy`, shipped in the
`@honua/mcp-server` package, bridges the two: it runs as the stdio server Claude
Desktop expects and forwards to your deployment.

Add this to `claude_desktop_config.json` (Settings -> Developer -> Edit Config):

```json
{
  "mcpServers": {
    "honua": {
      "command": "npx",
      "args": ["-y", "-p", "@honua/mcp-server", "honua-mcp-proxy"],
      "env": {
        "HONUA_MCP_REMOTE_URL": "https://your-honua.example.com/mcp",
        "HONUA_MCP_AUTH_TOKEN": "<bearer token>"
      }
    }
  }
}
```

`HONUA_MCP_REMOTE_URL` is required and is your deployment's `/mcp` endpoint;
`HONUA_MCP_URL` is accepted as an alias. For authentication set **exactly one**
scheme — `HONUA_MCP_AUTH_TOKEN` for a bearer token, or `HONUA_API_KEY` (or
`HONUA_ADMIN_KEY`) for an API key. Setting a bearer token and an API key
together is rejected at startup, as is setting both key variables. Whenever a
credential is configured the remote URL must be HTTPS.

Restart Claude Desktop, then ask it to list the Honua tools. If nothing appears,
check the logs: a missing `HONUA_MCP_REMOTE_URL` fails immediately with a named
error rather than starting an empty server.

> Do not confuse this with the standalone server in the same package. The
> `honua-mcp` binary serves any public ArcGIS or OGC endpoint and needs no Honua
> deployment; `honua-mcp-proxy` is the one that reaches *your* server. See
> [the standalone MCP server](https://github.com/honua-io/honua-sdk-js/blob/trunk/docs/mcp-server.md).

The connector must present a principal authorized to the target tenant and
Studio resources. Discover tools with `tools/list`; do not hard-code
the tool table as an authorization boundary. A safe turn is:

1. call `honua_studio_create_draft`;
2. apply typed mutations using the returned `generation`;
3. on `failed_precondition`, fetch, reconcile, and retry once;
4. validate and preview the draft;
5. save a version with `honua_studio_save_version`, and branch a new draft from one with `honua_studio_reopen_version` — both are MCP tools, so durable versioning does not need an SDK client. `honua_studio_save_version` returns the immutable `versionId` and `contentHash` that `honua_studio_propose_publication` requires.

`honua_studio_propose_publication` records intent, but do not promise a public
URL: the governed publication journey is blocked by
[#3304](https://github.com/honua-io/honua-server/issues/3304).
