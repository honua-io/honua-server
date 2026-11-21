#!/usr/bin/env bash
# Sourced by gp-lifecycle-harness.sh; sets its preflight_failure.

# The stack validates bearer tokens with the symmetric key it is given, so a run with no
# identity provider mints its own key and one token per tenant. Supplying some but not all
# of the five values is a configuration error, not a reason to mint.
qualification_tenant_tokens() {
  local name supplied=0
  local -a names=(HONUA_GP_TENANT_A_TOKEN HONUA_GP_TENANT_B_TOKEN HONUA_GP_OIDC_ISSUER HONUA_GP_OIDC_AUDIENCE HONUA_GP_OIDC_SIGNING_KEY)
  for name in "${names[@]}"; do [[ -z "${!name:-}" ]] || supplied=$((supplied + 1)); done
  (( supplied == ${#names[@]} )) && return 0
  if (( supplied > 0 )); then
    for name in "${names[@]}"; do
      [[ -n "${!name:-}" ]] || { preflight_failure="${name} is required when any tenant identity value is supplied"; return 1; }
    done
  fi
  export HONUA_GP_OIDC_ISSUER=https://gp-qualification.invalid HONUA_GP_OIDC_AUDIENCE=gp-lifecycle
  HONUA_GP_OIDC_SIGNING_KEY="$(python3 -c 'import secrets; print(secrets.token_urlsafe(48))')" || {
    preflight_failure="tenant signing key could not be generated"; return 1; }
  export HONUA_GP_OIDC_SIGNING_KEY
  HONUA_GP_TENANT_A_TOKEN="$(mint_tenant_token tenant-a)" && HONUA_GP_TENANT_B_TOKEN="$(mint_tenant_token tenant-b)" || {
    preflight_failure="tenant tokens could not be minted"; return 1; }
  export HONUA_GP_TENANT_A_TOKEN HONUA_GP_TENANT_B_TOKEN
}

mint_tenant_token() {
  HONUA_GP_TENANT_ROLE="$(tenant_role_name)" python3 - "$1" <<'PY'
import base64, hashlib, hmac, json, os, sys, time
def part(value):
    return base64.urlsafe_b64encode(value).rstrip(b"=")
def claims(value):
    return part(json.dumps(value, separators=(",", ":")).encode())
tenant, now = sys.argv[1], int(time.time())
signed = claims({"alg": "HS256", "typ": "JWT"}) + b"." + claims({
    "iss": os.environ["HONUA_GP_OIDC_ISSUER"], "aud": os.environ["HONUA_GP_OIDC_AUDIENCE"],
    "sub": f"gp-qualification-{tenant}", "tenant_id": tenant, "tid": tenant,
    "roles": [os.environ["HONUA_GP_TENANT_ROLE"]],
    "scope": "honua.mcp.execute honua.mcp.read",
    "iat": now, "nbf": now, "exp": now + 6 * 3600, "jti": f"{tenant}-{now}"})
key = os.environ["HONUA_GP_OIDC_SIGNING_KEY"].encode()
print((signed + b"." + part(hmac.new(key, signed, hashlib.sha256).digest())).decode())
PY
}

# Tenants need Process.Execute and Job.Read but never admin: the admin bypass would let
# tenant B read tenant A's job and void the cross-tenant non-disclosure check. Built-in
# editor/viewer roles grant no process execution, so the run provisions its own role.
# Externally supplied tenant tokens must carry this role name: set HONUA_GP_TENANT_ROLE to it.
tenant_role_name() {
  printf '%s' "${HONUA_GP_TENANT_ROLE:-gp-qualification-tenant-executor}"
}

provision_tenant_role() {
  local role id roles='/api/v1/admin/roles'
  role="$(tenant_role_name)"
  # Admin-equivalent roles void non-disclosure; reusing a built-in role would rewrite its grants.
  case "${role,,}" in
    admin|administrator|editor|viewer) echo "tenant role must be a dedicated non-admin role" >&2; return 1;;
  esac
  id="$(auth_curl "${base_url}${roles}" | jq -r --arg role "${role,,}" \
    '[.data[] | select((.name | ascii_downcase) == $role) | .roleId][0] // empty')" || return 1
  if [[ -z "${id}" ]]; then
    id="$(jq -nc --arg role "${role}" '{name:$role,description:"GP qualification tenant: execute processes, read jobs"}' |
      auth_curl -H 'Content-Type: application/json' --data-binary @- "${base_url}${roles}" | jq -re '.data.roleId')" || return 1
  fi
  auth_curl -X PUT -H 'Content-Type: application/json' "${base_url}${roles}/${id}/permissions" -o /dev/null -d \
    '{"permissions":[{"service":"Process","layer":"*","operation":"Execute"},{"service":"Job","layer":"*","operation":"Read"}]}'
}
