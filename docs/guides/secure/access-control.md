# Control access to services and layers

Decide who can read and who can write each published service and layer, using access policies for coarse rules and roles with per-operation grants for fine-grained RBAC.

**Prerequisites:** An admin API key ([Authenticate clients](authentication.md)) and a published service. Open the local API explorer at `http://localhost:8080/docs` and authorize it with the admin key before running these operations.

Two mechanisms compose: a per-resource **access policy** (`allowAnonymous`, `allowAnonymousWrite`, `allowedRoles`, `allowedWriteRoles`) and **RBAC roles** carrying `(service, layer, operation)` permission grants with `*` wildcards. An explicit write policy on a resource stays authoritative; when none is set, a matching RBAC write grant (`insert`/`update`/`delete`), a global data-editor role (`Rbac__DataEditorRoles`), or a service-scoped `data-editor:{service}` role authorizes the mutation. Admin endpoints (`/api/v1/admin/*`) and metrics endpoints always require the `admin` role.

## Steps

### 1. Restrict writes on a service

In the [API explorer](../../reference/openapi-and-explorer.md), run `PUT /api/v1/admin/services/{service}/access-policy` with `{service}` set to `parks` and this body:

```json
{
  "allowAnonymous": true,
  "allowAnonymousWrite": false,
  "allowedWriteRoles": ["editors"]
}
```

Reads stay public; writes now require the `editors` role. Omit `allowedRoles`/`allowedWriteRoles` fields you don't want to change — only supplied fields are patched. Per-layer metadata (including layer-level policy) is managed via `PUT /api/v1/admin/services/{serviceName}/layers/{layerId}/metadata`.

### 2. Create a role

Run `POST /api/v1/admin/roles` in the explorer with this body:

```json
{
  "name": "editors",
  "description": "Field data editors"
}
```

Note the returned role `id`. `GET /api/v1/admin/roles` lists all roles; `PUT`/`DELETE /api/v1/admin/roles/{id}` rename or remove one.

### 3. Grant per-operation permissions to the role

Run `PUT /api/v1/admin/roles/{roleId}/permissions`, substituting the role id returned in step 2, with this body:

```json
{
  "permissions": [
    { "service": "parks", "layer": "*", "operation": "update" },
    { "service": "parks", "layer": "*", "operation": "insert" }
  ]
}
```

Operations: `query` (read), `insert`, `update`, `delete`, `export`, `metadata`, `admin`, or `*`. `service` and `layer` accept `*`; a service-level grant (`layer:"*"`) implies all its layers. Grants never hard-deny — when none matches, the resource's access policy decides.

### 4. Assign roles to users

Run `PUT /api/v1/admin/users/{userId}/roles`, substituting the user's OIDC id, with this body:

```json
{
  "roles": ["editors"]
}
```

The list replaces the user's role set. `GET /api/v1/admin/users` lists known users (OIDC sign-ins appear after first login). Roles also flow from your IdP's token claims (`Rbac__RoleClaimType`, default `roles`) and from API-key permission labels.

### 5. Restrict which rows a role can see (row-level security)

RBAC grants control whether a role can query a layer; **row-level security (RLS)** narrows *which features* the query returns. An RLS policy attaches a row-visibility predicate — a layer attribute compared against the caller's claim — to a `(role, service, layer)` scope. Matching policies are AND-ed into the query `WHERE` clause server-side, pushed to PostgreSQL, and applied identically across every query protocol (GeoServices REST, OGC API Features, OData).

Run `POST /api/v1/admin/rls-policies` with this body:

```json
{
  "role": "*",
  "service": "*",
  "layer": "*",
  "attribute": "region",
  "claimType": "region",
  "comparison": "in"
}
```

With this policy in place, a caller whose token carries `region=west` sees only features where `region = 'west'`; a caller with `region=east` sees only `east` features. The claim value is bound as a query parameter, so it can never inject SQL. `comparison` is `in` (default — matches any of the caller's claim values) or `equals`. Use `*` wildcards on `role`/`service`/`layer` to scope broadly, or concrete names to target one layer. `GET /api/v1/admin/rls-policies` lists policies; `DELETE /api/v1/admin/rls-policies/{id}` removes one.

RLS is **fail-secure**: if a matching policy exists but the caller carries no value for the referenced claim, the predicate hides every row rather than revealing them. RLS composes with (and is independent of) a layer's always-on metadata permanent filter — both are AND-ed together.

RLS controls which *rows* a role sees. Restricting which *fields* (columns) a role can read — field-level masking — is tracked separately and not yet available.

## Verify

Run `GET /api/v1/admin/users/{userId}/effective-permissions` in the explorer.

```json
{ "success": true, "data": { "userId": "…", "roles": ["editors"], "permissions": [ { "service": "parks", "layer": "*", "operation": "update" } ] } }
```

Then confirm enforcement: an anonymous `POST .../FeatureServer/0/addFeatures` against the restricted service returns `401`/`403`, while a caller holding `editors` succeeds. API keys have the same introspection at `GET /api/v1/admin/api-keys/{id}/effective-permissions`.

## Troubleshoot

| Symptom | Fix |
|---|---|
| Editor with a write grant still gets `403` | An explicit `allowedWriteRoles` on the resource overrides RBAC grants by design; add the role to the policy or clear the explicit write policy. |
| Anonymous reads broke after setting a policy | `allowAnonymous:false` (or any `allowedRoles` list without anonymous) gates reads too; set `allowAnonymous:true` to keep public reads. |
| Roles from the IdP are ignored | The token's role claim must match `Rbac__RoleClaimType` (default `roles`); adjust the IdP claim mapping or the setting. |
| Write allowed that you expected blocked | Check global `Rbac__DataEditorRoles` and `data-editor:{service}`-prefixed roles — both grant writes when no explicit write policy applies. |
| RLS returns zero rows unexpectedly | The caller carries no value for the policy's `claimType` (RLS is fail-secure). Confirm the IdP emits that claim, or that the policy's `attribute`/`claimType` match the data and token. |

More general failures: [Troubleshooting](../deploy/troubleshooting.md).

## Next steps

- [Production security checklist](production-checklist.md)
- [Authenticate clients](authentication.md)
- [Edit features](../edit/edit-features.md)
