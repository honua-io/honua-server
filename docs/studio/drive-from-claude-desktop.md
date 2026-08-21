# Drive Studio from Claude Desktop

Claude Desktop can edit the same server draft that standalone or embedded
Studio displays. Configure a Streamable HTTP-capable MCP bridge for the Honua
server's `/mcp` endpoint. One common bridge configuration is:

```json
{
  "mcpServers": {
    "honua": {
      "command": "npx",
      "args": [
        "-y",
        "mcp-remote",
        "https://honua.example.com/mcp",
        "--header",
        "Authorization: Bearer ${HONUA_ACCESS_TOKEN}"
      ],
      "env": {
        "HONUA_ACCESS_TOKEN": "replace-with-a-short-lived-token"
      }
    }
  }
}
```

Prefer your bridge's OS keychain/OAuth support over a token in the JSON file.
Restart Claude Desktop, confirm the Honua tools appear, then ask it to create or
open a draft. Open that `draftId` in Studio; both clients now operate on the same
composition and generation.

If Claude reports `failed_precondition`, another client changed the draft. It
must call `honua_studio_get_draft`, reconcile, and retry with the returned
generation. The final `honua_studio_propose_publication` call only records
intent on the draft; it does **not** create a publication request. Call
`honua_studio_save_version` with the current `draftId` and `generation`, capture
its immutable `itemId` and `versionId`, then have the Studio client call
`POST /api/v1/studio/content-items/{itemId}/versions/{versionId}/publish-requests`
to mint the pollable request handle. Complete approval in Console, then poll
that request until a `published` response includes `publicUrl`.

For the complete transport, authentication, and operator-grant posture, see
[Connect AI agents to Honua over MCP](../guides/connect/ai-agents-mcp.md).
