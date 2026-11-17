#!/usr/bin/env python3
"""Tenant tokens carry a non-admin execute role the harness provisions."""

import base64
import json
import os
import re
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
TENANT_TOKENS = ROOT / "scripts/qualification/gp-tenant-tokens.sh"
HARNESS = ROOT / "scripts/qualification/gp-lifecycle-harness.sh"
EXPECTED_GRANTS = [
    {"service": "Process", "layer": "*", "operation": "Execute"},
    {"service": "Job", "layer": "*", "operation": "Read"},
]

# A fake admin API: records each call and answers like the server's {success,data} envelope.
FAKE_AUTH_CURL = r'''
base_url=http://server
auth_curl() {
  local method="" body="" url
  while (($#)); do
    case "$1" in
      -X) method="$2"; shift 2;;
      -d) body="$2"; shift 2;;
      --data-binary) body="$(cat)"; shift 2;;
      -H|-o) shift 2;;
      *) url="$1"; shift;;
    esac
  done
  [[ -n "$method" ]] || { [[ -n "$body" ]] && method=POST || method=GET; }  # curl implies POST with a body
  printf '%s %s %s\n' "$method" "${url#http://server}" "$body" >> calls
  case "$method ${url#http://server}" in
    "GET /api/v1/admin/roles") cat roles.json;;
    "POST /api/v1/admin/roles") echo '{"success":true,"data":{"roleId":"new-id"}}';;
    PUT*) echo '{"success":true,"data":[]}';;
  esac
}
'''


def functions(*names):
    source = TENANT_TOKENS.read_text(encoding="utf-8")
    return "\n".join(re.search(rf"^{name}\(\) \{{\n.*?^\}}", source, re.M | re.S).group(0) for name in names)


def run(script, roles):
    env = {k: v for k, v in os.environ.items() if not k.startswith(("HONUA_GP_TENANT_", "HONUA_GP_OIDC_"))}
    with tempfile.TemporaryDirectory() as directory:
        Path(directory, "roles.json").write_text(json.dumps({"success": True, "data": roles}), encoding="utf-8")
        completed = subprocess.run(["bash", "-c", "set -uo pipefail\n" + script], cwd=directory, env=env,
                                   capture_output=True, text=True, check=False)
        calls = Path(directory, "calls")
        lines = calls.read_text(encoding="utf-8").splitlines() if calls.exists() else []
        return completed, lines


def provision(roles, prefix=""):
    return run(functions("tenant_role_name", "provision_tenant_role") + FAKE_AUTH_CURL + prefix
               + "provision_tenant_role", roles)


class TenantRoleTests(unittest.TestCase):
    def test_minted_tokens_carry_only_the_non_admin_tenant_role(self):
        completed, _ = run(functions("tenant_role_name", "mint_tenant_token") + r'''
export HONUA_GP_OIDC_ISSUER=i HONUA_GP_OIDC_AUDIENCE=a HONUA_GP_OIDC_SIGNING_KEY=k
mint_tenant_token tenant-a''', [])
        self.assertEqual(0, completed.returncode, completed.stderr)
        payload = completed.stdout.strip().split(".")[1]
        claims = json.loads(base64.urlsafe_b64decode(payload + "=" * (-len(payload) % 4)))
        self.assertEqual((["gp-qualification-tenant-executor"], "tenant-a"), (claims["roles"], claims["tenant_id"]))

    def test_missing_role_is_created_then_granted_execute_and_job_read(self):
        completed, calls = provision([{"roleId": "admin-id", "name": "admin"}])
        self.assertEqual(0, completed.returncode, completed.stderr)
        self.assertEqual(["GET /api/v1/admin/roles", "POST /api/v1/admin/roles"], [" ".join(c.split()[:2]) for c in calls[:2]])
        self.assertEqual("gp-qualification-tenant-executor", json.loads(calls[1].split(" ", 2)[2])["name"])
        method, path, body = calls[2].split(" ", 2)
        self.assertEqual(("PUT", "/api/v1/admin/roles/new-id/permissions"), (method, path))
        self.assertEqual(EXPECTED_GRANTS, json.loads(body)["permissions"])

    def test_existing_role_is_reused_and_its_grants_reset(self):
        completed, calls = provision([{"roleId": "r-1", "name": "GP-Qualification-Tenant-Executor"}])
        self.assertEqual(0, completed.returncode, completed.stderr)
        self.assertEqual(2, len(calls))
        self.assertFalse(any(call.startswith("POST") for call in calls))
        self.assertTrue(calls[1].startswith("PUT /api/v1/admin/roles/r-1/permissions "))

    def test_admin_or_built_in_roles_are_never_provisioned_as_the_tenant_role(self):
        for role in ("Admin", "administrator", "editor", "Viewer"):
            with self.subTest(role=role):
                completed, calls = provision([], prefix=f"HONUA_GP_TENANT_ROLE={role}\n")
                self.assertNotEqual(0, completed.returncode)
                self.assertEqual([], calls)

    def test_tenant_scenario_provisions_the_role_before_submitting(self):
        source = HARNESS.read_text(encoding="utf-8")
        body = re.search(r"^run_tenant_limits\(\) \{\n.*?^\}", source, re.M | re.S).group(0)
        self.assertLess(body.index("provision_tenant_role"), body.index("tenant_curl"))


if __name__ == "__main__":
    unittest.main()
