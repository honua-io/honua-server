# Admin CLI

`honua admin` is the deterministic command-line projection of the canonical Admin
OpenAPI document. It does not maintain a hand-written second client. Generated
request models, operation ids, validation, and the release inventory gate keep
the Admin API, operation catalog, MCP publication, SDK client, and CLI aligned.

> **Source of truth:** the command tree and help text are generated and shipped
> by `@honua/sdk-js`. This page records the candidate output for discoverability;
> `honua admin --help` and `honua admin <group> --help` from the installed pinned
> SDK are authoritative. Do not add a server-only command or alias here.

## Install and authenticate

```bash
npm install --global @honua/sdk-js
export HONUA_BASE_URL=https://honua.example.com
export HONUA_ADMIN_KEY="$ADMIN_KEY_FROM_SECRET_MANAGER"
```

The dedicated admin key takes precedence over the general `HONUA_API_KEY`. Never
put either value in a command argument, body file, or repository.

## Grammar

```text
honua admin <connect|import|publish|configure|secure|release|operate> \
  <operationId> \
  [--body @file.json] \
  [--path name=value]... \
  [--query name=value]... \
  [--dry-run | --yes] \
  [--json]
```

Use `honua admin api <operationId> ...` as the complete operation-id escape
hatch. The named groups are navigation aliases; they do not change the wire
operation or its authorization.

- `--body @file.json` reads JSON without putting it in shell history. Inline JSON
  is accepted for small non-secret inputs.
- Repeat `--path` and `--query` for parameter bindings.
- `--dry-run` requests a no-effect preview when the operation supports one.
- `--yes` is the explicit mutation acknowledgement for non-interactive use.
- `--json` emits the generated response envelope for scripts and receipts.

## Examples

```bash
honua admin connect createConnection \
  --body @connection.json --yes --json

honua admin connect testConnection \
  --path id=local --yes --json

honua admin publish publishLayer \
  --path id=local --body @layer.json --yes --json

honua admin configure updateServiceAccessPolicy \
  --path serviceName=default \
  --body '{"allowAnonymous":true}' --yes --json

honua admin secure getAdminApiKeyEffectivePermissions \
  --path id=KEY_ID --json
```

Use the exact `operationId` from the generated
[Admin OpenAPI document](overview.md). Multipart file upload may be performed
directly against the Admin API when the generated command does not expose a file
binding.

## Approval outcomes

A command can return a governed `DryRunFirst` or `RequiresApproval` result instead
of applying immediately. Preserve the proposal id, inspect it in Console, and let
a different authorized human decide it. Do not repeat the operation with a raw
request to evade policy.

For Console itself, use the focused
`["admin:read","admin:approve"]` recipe in
[Focused Console operation](../../guides/operate/focused-console.md), not a general
admin write key.
