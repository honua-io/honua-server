# Set up Honua with an AI agent

Use a local MCP-capable agent to provision or connect to Honua, configure the
server through the canonical control plane, and hand protected actions to a
human approval lane. The Admin API, operation catalog, `honua admin`, and
`honua_admin_*` MCP tools are four projections of one operation inventory.

**Prerequisites:** a Docker or AWS ECS deployment, an admin credential for
bootstrap, Node.js 20.19 or newer, and an MCP-capable agent host.

## Choose the deployment path

| Path | Provisioning authority | MCP handoff |
| --- | --- | --- |
| Docker Compose | Local user and Docker daemon | `http://localhost:8080/mcp` |
| AWS ECS/Fargate | The certified Honua IaC/devops plan and an explicitly approved cloud apply | The ALB's TLS origin plus `/mcp` |

For local setup, follow the [quickstart](../../get-started/quickstart.md). For
AWS, first follow the certified ECS/Fargate pattern in
[Cloud deployments](../deploy/cloud-deployments.md): arm64 Fargate behind an ALB,
RDS PostgreSQL/PostGIS, ElastiCache Redis, and secrets injected from Secrets
Manager or SSM. Ask the infrastructure agent to show region, resources, estimated
cost, credential source, and rollback plan before approving apply. Do not accept
an unreviewed free-form cloud mutation.

Both paths must finish with a healthy HTTPS origin, `/healthz/ready`, `/mcp`, the
Admin API, and the Console URL. The agent should return exact candidate image and
component pins with the handoff.

## Connect the agent

Honua serves MCP over Streamable HTTP. HTTP-capable clients connect directly to
`https://honua.example.com/mcp`. Stdio-only clients use the transport-symmetric
proxy from `@honua/mcp-server`:

```bash
npm install --global @honua/mcp-server
export HONUA_MCP_REMOTE_URL=https://honua.example.com/mcp
export HONUA_ADMIN_KEY="$HONUA_AGENT_ADMIN_KEY"
honua-mcp-proxy
```

`HONUA_MCP_URL` is an alias for `HONUA_MCP_REMOTE_URL`. OAuth clients may set
`HONUA_MCP_AUTH_TOKEN` instead. `HONUA_API_KEY` remains the general-key fallback,
but admin bootstrap and configuration should use the dedicated
`HONUA_ADMIN_KEY`. The proxy forwards the remote catalog; it does not maintain a
second tool inventory.

Example MCP client configuration:

```json
{
  "mcpServers": {
    "honua": {
      "command": "honua-mcp-proxy",
      "env": {
        "HONUA_MCP_REMOTE_URL": "https://honua.example.com/mcp",
        "HONUA_ADMIN_KEY": "${HONUA_AGENT_ADMIN_KEY}"
      }
    }
  }
}
```

Use your agent host's secret/environment facility for the actual value. Do not
commit an expanded key into `.mcp.json`.

## Protect credentials

Tool arguments may carry secret **references**, never secret values. An admin
connection operation advertises a `secret_ref` property whose value is an opaque
reference understood by the configured secret provider. The Admin REST shape
uses `secretReference` plus `secretType` for the same boundary.

- Never paste database, model-provider, cloud, or admin secrets into a prompt.
- Never ask the model to echo, resolve, summarize, or validate a secret's value.
- Keep the root `HONUA_ADMIN_PASSWORD` for bootstrap and break-glass use. Mint a
  scoped agent key after setup.
- Treat tool results, approval plans, logs, and audit records as secret-free.
- Rotate any credential that entered a transcript.

## Discover the live contract

Start every session by calling `tools/list`, `honua_list_capabilities`, and the
`set_up_and_publish` prompt. On the 2026.1 server profile:

- deterministic `admin.*` descriptors are published as `honua_admin_*` by
  default;
- AI-assisted operation descriptors are not published;
- annotations identify read-only, destructive, and idempotent behavior;
- the input schema is derived from the canonical Admin OpenAPI operation;
- calls dispatch through the same authorization, policy, validation, executor,
  approval, and audit seams as the Admin API.

The `admin-mcp` capability is Preview in 2026.1. The candidate inventory gate,
not a remembered tool count in a prompt, decides whether an Admin API operation
is missing from the catalog, MCP projection, generated client, or CLI.

## Run the setup journey

Give the agent a bounded request:

> Inspect capabilities and health. Create and test the connection using
> `secret_ref`, import the named source, publish it to the `default` service,
> configure the minimum access policy, run `geometry.buffer`, create and save a
> Studio map draft from the published layer and result, and propose publication.
> Dry-run whenever supported. Stop for credentials, cost, deletion, public-access
> expansion, or publication. Return all resource, job, proposal, and audit ids.

The `set_up_and_publish` prompt supplies the same nine beats. A safe agent should:

1. Read capabilities, readiness, and release state.
2. Create and test a connection without observing secret material.
3. Import or register the source.
4. Publish and configure a service/layer.
5. Set the minimum access/key policy.
6. Discover and run geoprocessing.
7. Compose a Studio draft.
8. Validate, preview, and save it durably.
9. Propose protected publication and wait for the Console decision receipt.

## Understand protected outcomes

Destructive or deployment-scoped admin descriptors are protected by the operation
policy even when no custom rule set is enabled. If the operation supports
dry-run, the first call returns `DryRunFirst`; otherwise it returns
`RequiresApproval` in the `admin` lane. These are structured outcomes, not MCP
transport errors.

The agent must not approve its own work or work around the policy with raw HTTP.
Open the returned proposal id in Console, review plan/diff/dry-run/risk, and have
a different authorized operator approve or reject it. Preserve the resulting
audit id with the journey receipt.

## Deterministic CLI equivalent

Use `honua admin` when a human or CI job needs the same generated contract without
an agent:

```bash
honua admin connect createConnection --body @connection.json --yes --json
honua admin connect testConnection --path id=local --yes --json
honua admin publish publishLayer --path id=local --body @layer.json --yes --json
```

The command grammar and safety flags are in the
[Admin CLI reference](../../reference/admin-api/cli.md).

## Verify the handoff

Record:

- deployment origin, Console URL, and exact component/image pins;
- readiness and MCP initialize receipts;
- operation inventory/catalog version;
- connection, service, layer, GP job/result, and Studio draft/content ids;
- proposal decision and audit ids;
- the approved cloud plan and cost estimate for an AWS path.

Then use [Focused Console operation](../operate/focused-console.md) to inspect the
same identifiers rather than recreating the configuration through forms.

## Troubleshoot

- **Proxy exits immediately** — set an absolute HTTP(S)
  `HONUA_MCP_REMOTE_URL`, including `/mcp`.
- **`unauthenticated`** — attach `HONUA_ADMIN_KEY` or
  `HONUA_MCP_AUTH_TOKEN`; `HONUA_API_KEY` is the general-key fallback. A
  successful anonymous initialize does not authorize calls.
- **Admin tools are missing** — verify the server has the admin operation catalog
  composed and `Mcp__PublishOperations__AdminFamilyEnabled=true`, then restart.
- **An AI-assisted descriptor is missing** — intentional by default. Setting
  `Mcp__PublishOperations__DeterministicOnly=false` expands the trust boundary and
  should be a reviewed deployment decision.
- **AWS jobs or proposals return 503** — the control plane requires reachable
  Redis. Restore ElastiCache before retrying.
