# Connect AI agents to Honua over MCP

Point any MCP-capable agent (Claude Code, Claude Desktop, or your own client) at Honua's built-in MCP endpoint to plan, validate, dry-run, and execute geoprocessing work with the same authorization rules as every other protocol.

**Prerequisites:** a running server ([quickstart](../../get-started/quickstart.md)) and a published layer ([publish layers](../publish/publish-layers.md)). Tool calls require an authenticated identity — see [authentication](../secure/authentication.md).

The endpoint is `POST /mcp`: JSON-RPC 2.0 over HTTP (single requests and batches), MCP protocol revision `2025-03-26`. The handshake methods (`initialize`, `tools/list`, `resources/list`, `resources/templates/list`) are open; `tools/call` and `resources/read` require an authenticated principal plus the matching operator grant.

## Steps

1. Confirm the endpoint answers:

   ```bash
   BASE=http://localhost:8080
   curl -s -X POST "$BASE/mcp" -H "Content-Type: application/json" -d '{
     "jsonrpc": "2.0", "id": 1, "method": "initialize",
     "params": { "protocolVersion": "2025-03-26", "capabilities": {},
                 "clientInfo": { "name": "curl", "version": "0" } }
   }'
   ```

2. Register the server with your MCP client. For Claude Code, add to the project's `.mcp.json`:

   ```json
   {
     "mcpServers": {
       "honua": {
         "type": "http",
         "url": "http://localhost:8080/mcp",
         "headers": { "X-API-Key": "${HONUA_API_KEY}" }
       }
     }
   }
   ```

   Any client that speaks the MCP HTTP transport works the same way; send credentials as the `X-API-Key` header (or your deployment's bearer token) on every request.

3. Ask the agent to list tools and start with the safe ones — everything in the planning family is read-only:

   - `honua_ground_candidates` / `honua_clarify_intent` — turn a natural-language goal into a drafted intent with candidate datasets and processes
   - `honua_plan_analysis` — draft an executable plan from an intent
   - `honua_validate_plan` — static validation: returns `isExecutable`, `requiresApproval`, violations, and warnings
   - `honua_dry_run_plan` — estimates duration, artifacts, and side effects without executing
   - `honua_validate_package` / `honua_preview_package` — review a map/app package before execute or publish

4. Execute and manage jobs once a plan validates:

   - `honua_execute_plan` — submits the plan (supports an `idempotencyKey`); returns a `jobId` and a `honua://jobs/{jobId}` resource URI
   - `honua_cancel_job` — requests cancellation by `jobId`

5. Read results through resources (`resources/read`):

   - `honua://catalog/processes` — the process catalog the planner can draw from
   - `honua://jobs/{jobId}` — live job status, phase, and percent complete
   - `honua://jobs/{jobId}/results` — the result package for a terminal job
   - `honua://jobs/{jobId}/report` — a structured analysis report for the same job
   - `honua://workspaces/{workspaceId}` — workspace lifecycle for job outputs

   Additional published-service/deployment/package resources exist but are opt-in surfaces that hosts enable explicitly; they are not advertised by a default deployment.

## Verify

```bash
BASE=http://localhost:8080
curl -s -X POST "$BASE/mcp" -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/list"}'
```

The response lists the nine `honua_*` tools above with JSON Schema input definitions. From your agent, "list the Honua tools and validate an empty plan" should return a structured violation list (for example `EMPTY_PLAN_ID`), not an error.

## Troubleshoot

- **`unauthenticated` on `tools/call` or `resources/read`** — handshake methods work anonymously but tool calls do not; attach the `X-API-Key` header (or token) to the client config. See [troubleshooting](../deploy/troubleshooting.md).
- **`permission_denied`** — the identity authenticates but lacks the operator grant for that tool family; grant the relevant operator permission to the calling identity.
- **HTTP 202 with an empty body** — not an error: MCP notifications (`notifications/*` without an `id`) are acknowledged with 202 by design.
- **`invalid_request` (-32600)** — malformed JSON-RPC envelope; common causes are a missing `id` on a non-notification method or batching the `initialize` call (it must be sent alone).
- **Agent "succeeds" but reports a tool error** — tool failures are returned inside `result` with `isError: true` and a structured `code` (`invalid_argument`, `not_found`, `failed_precondition`, …) per the MCP error contract; read the embedded message.

A separate read-only discovery/query MCP server (`@honua/mcp-server`, from the `honua-sdk-js` repository) is also available for agents that only need to browse services and query features rather than run operator workflows.

## Next steps

- [Run geoprocessing](../query-analyze/run-geoprocessing.md)
- [Authentication](../secure/authentication.md)
- [Protocol overview](../../concepts/protocols.md)
