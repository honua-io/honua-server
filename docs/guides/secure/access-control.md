# Control access to services and layers

Decide who can read and who can write each published service and layer, using access policies for coarse rules and roles with per-operation grants for fine-grained RBAC.

**Prerequisites:** An admin API key ([Authenticate clients](authentication.md)) and a published service. Examples use `$HONUA_ADMIN_PASSWORD` and `http://localhost:8080`.

Two mechanisms compose: a per-resource **access policy** (`allowAnonymous`, `allowAnonymousWrite`, `allowedRoles`, `allowedWriteRoles`) and **RBAC roles** carrying `(service, layer, operation)` permission grants with `*` wildcards. An explicit write policy on a resource stays authoritative; when none is set, a matching RBAC write grant (`insert`/`update`/`delete`), a global data-editor role (`Rbac__DataEditorRoles`), or a service-scoped `data-editor:{service}` role authorizes the mutation. Admin endpoints (`/api/v1/admin/*`) and metrics endpoints always require the `admin` role.

## Steps

### 1. Restrict writes on a service

```bash
BASE=http://localhost:8080
SERVICE=parks
curl -X PUT "$BASE/api/v1/admin/services/$SERVICE/access-policy" \
  -H "X-API-Key: $HONUA_ADMIN_PASSWORD" -H "Content-Type: application/json" \
  -d '{"allowAnonymous":true,"allowAnonymousWrite":false,"allowedWriteRoles":["editors"]}'
```

Reads stay public; writes now require the `editors` role. Omit `allowedRoles`/`allowedWriteRoles` fields you don't want to change — only supplied fields are patched. Per-layer metadata (including layer-level policy) is managed via `PUT /api/v1/admin/services/{serviceName}/layers/{layerId}/metadata`.

### 2. Create a role

```bash
curl -X POST "$BASE/api/v1/admin/roles" \
  -H "X-API-Key: $HONUA_ADMIN_PASSWORD" -H "Content-Type: application/json" \
  -d '{"name":"editors","description":"Field data editors"}'
```

Note the returned role `id`. `GET /api/v1/admin/roles` lists all roles; `PUT`/`DELETE /api/v1/admin/roles/{id}` rename or remove one.

### 3. Grant per-operation permissions to the role

```bash
ROLE_ID=<paste-the-role-id>
curl -X PUT "$BASE/api/v1/admin/roles/$ROLE_ID/permissions" \
  -H "X-API-Key: $HONUA_ADMIN_PASSWORD" -H "Content-Type: application/json" \
  -d '{"permissions":[{"service":"parks","layer":"*","operation":"update"},{"service":"parks","layer":"*","operation":"insert"}]}'
```

Operations: `query` (read), `insert`, `update`, `delete`, `export`, `metadata`, `admin`, or `*`. `service` and `layer` accept `*`; a service-level grant (`layer:"*"`) implies all its layers. Grants never hard-deny — when none matches, the resource's access policy decides.

### 4. Assign roles to users

```bash
USER_ID=<oidc-user-id>
curl -X PUT "$BASE/api/v1/admin/users/$USER_ID/roles" \
  -H "X-API-Key: $HONUA_ADMIN_PASSWORD" -H "Content-Type: application/json" \
  -d '{"roles":["editors"]}'
```

The list replaces the user's role set. `GET /api/v1/admin/users` lists known users (OIDC sign-ins appear after first login). Roles also flow from your IdP's token claims (`Rbac__RoleClaimType`, default `roles`) and from API-key permission labels.

## Verify

```bash
curl -H "X-API-Key: $HONUA_ADMIN_PASSWORD" \
  "$BASE/api/v1/admin/users/$USER_ID/effective-permissions"
```

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

More general failures: [Troubleshooting](../deploy/troubleshooting.md).

## Next steps

- [Production security checklist](production-checklist.md)
- [Authenticate clients](authentication.md)
- [Edit features](../edit/edit-features.md)
