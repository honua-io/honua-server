"""#4577 second exchange path: opt-in OAuth2 client_credentials over verified HTTPS.

The grant exchanges an API key for a portal token that should carry the key's
own authority. A scoped key must not come out broader than it went in.
Requires Authentication__PortalToken__OAuth2__EnableClientCredentials=true.
"""
import hashlib
import json
import ssl
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

BASE = "https://candidate.honua.test:8443"
CTX = ssl.create_default_context(cafile="/work/root.crt")
ADMIN = open("/work/admin.pw").read().strip()
PROTECTED, SCOPED = "arcgis_compat_protected", "arcgis_compat_scoped"
LAYER = {PROTECTED: 2010, SCOPED: 2020}

receipt = {"requests": [], "checks": []}
failures = []


def call(method, path, *, headers=None, form=None, body=None, label):
    data, hdrs = None, dict(headers or {})
    if form is not None:
        data = urllib.parse.urlencode(form).encode()
        hdrs["Content-Type"] = "application/x-www-form-urlencoded"
    elif body is not None:
        data = json.dumps(body).encode()
        hdrs["Content-Type"] = "application/json"
    req = urllib.request.Request(BASE + path, data=data, method=method, headers=hdrs)
    try:
        with urllib.request.urlopen(req, context=CTX, timeout=30) as r:
            status, raw = r.status, r.read()
    except urllib.error.HTTPError as e:
        status, raw = e.code, e.read()
    try:
        doc = json.loads(raw) if raw else None
    except ValueError:
        doc = None
    receipt["requests"].append({
        "label": label, "method": method, "path": path.split("?")[0], "status": status,
        "error": (doc.get("error") if isinstance(doc, dict) and isinstance(doc.get("error"), str) else
                  (doc or {}).get("error", {}).get("code") if isinstance(doc, dict) else None),
        "tokenIssued": isinstance(doc, dict) and ("access_token" in doc or "token" in doc),
        "bodySha256": hashlib.sha256(raw).hexdigest(),
    })
    return status, doc


def check(name, ok, detail):
    receipt["checks"].append({"check": name, "pass": bool(ok), "detail": detail})
    if not ok:
        failures.append(name)


def names(doc):
    if not isinstance(doc, dict) or "features" not in doc:
        return []
    return sorted(n for n in ((f.get("attributes") or f.get("properties") or {}).get("name") for f in doc["features"]) if n)


def denied(status, doc):
    code = (doc or {}).get("error", {}).get("code") if isinstance(doc, dict) and isinstance(doc.get("error"), dict) else None
    return (status in (401, 403) or code in (401, 403, 498, 499)) and not names(doc)


def reads(prefix, token):
    auth = {"Authorization": "Bearer " + token}
    out = {}
    for svc in (PROTECTED, SCOPED):
        out[("fs", svc)] = call("GET", f"/rest/services/{svc}/FeatureServer/{LAYER[svc]}/query?where=1%3D1&outFields=*&f=json",
                                headers=auth, label=f"{prefix}-fs-{svc}")
        out[("ogc", svc)] = call("GET", f"/ogc/features/collections/{LAYER[svc]}/items?f=json",
                                 headers=auth, label=f"{prefix}-ogc-{svc}")
    return out


def create_key(name, permissions, expires_in=600):
    exp = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(time.time() + expires_in))
    s, d = call("POST", "/api/v1/admin/api-keys", headers={"X-API-Key": ADMIN},
                body={"name": name, "permissions": permissions, "expiresAt": exp}, label=f"create-key-{name}")
    assert s in (200, 201), (name, s)
    return d["data"]["apiKey"]["id"], d["data"]["key"]


def grant(client_id, secret, label):
    return call("POST", "/sharing/rest/oauth2/token", label=label,
                form={"grant_type": "client_credentials", "client_id": client_id, "client_secret": secret})


# Expected authority per credential, taken from the direct API-key transport
# (the canonical boundary), not from the token path under test.
constrained = {
    "cc-scoped-read": [f"read:{SCOPED}"],
    "cc-admin-read": ["admin:read"],
    "cc-ops-read": ["ops:read"],
    "cc-unknown": ["unrecognized:permission"],
}
created = {n: create_key(n, p) for n, p in constrained.items()}
full_id, full_key = create_key("cc-full-admin", ["admin:*"])

for name, (key_id, material) in created.items():
    direct = {}
    for svc in (PROTECTED, SCOPED):
        direct[("fs", svc)] = call("GET", f"/rest/services/{svc}/FeatureServer/{LAYER[svc]}/query?where=1%3D1&outFields=*&f=json",
                                   headers={"X-API-Key": material}, label=f"{name}-direct-fs-{svc}")
        direct[("ogc", svc)] = call("GET", f"/ogc/features/collections/{LAYER[svc]}/items?f=json",
                                    headers={"X-API-Key": material}, label=f"{name}-direct-ogc-{svc}")
    s, d = grant(name, material, f"{name}-grant")
    token = (d or {}).get("access_token") if isinstance(d, dict) else None
    receipt.setdefault("grants", {})[name] = {"status": s, "issued": bool(token)}
    if not token:
        # A refusal is not a widening; record it as such.
        check(f"{name}: client_credentials issued no broader token (refused)", True, {"status": s})
        continue
    via_token = reads(f"{name}-token", token)
    for k in direct:
        ds, dd = direct[k]
        ts, td = via_token[k]
        direct_ok = not denied(ds, dd) and bool(names(dd))
        token_ok = not denied(ts, td) and bool(names(td))
        check(f"{name}: token {k[0]} {k[1]} no broader than direct key",
              token_ok <= direct_ok,
              {"direct": {"status": ds, "features": names(dd)}, "token": {"status": ts, "features": names(td)}})

s, d = grant("cc-full-admin", full_key, "cc-full-admin-grant")
token = (d or {}).get("access_token") if isinstance(d, dict) else None
check("full-admin key: client_credentials issues a token", s == 200 and bool(token), {"status": s})
if token:
    for (proto, svc), (ts, td) in reads("cc-full-admin-token", token).items():
        expected = ["protected-alpha"] if svc == PROTECTED else ["scoped-alpha"]
        check(f"full-admin token {proto} reads {svc}", ts == 200 and names(td) == expected, {"status": ts, "features": names(td)})

rev_id, rev_key = create_key("cc-revoked", ["admin:*"])
call("POST", f"/api/v1/admin/api-keys/{rev_id}/revoke", headers={"X-API-Key": ADMIN}, label="revoke")
exp_id, exp_key = create_key("cc-expiring", ["admin:*"], expires_in=8)
time.sleep(10)
for name, cid, secret in (("revoked", "cc-revoked", rev_key), ("expired", "cc-expiring", exp_key),
                          ("invalid", "cc-full-admin", "hnua_notarealkey0000000000000000")):
    s, d = grant(cid, secret, f"{name}-grant")
    check(f"client_credentials rejects {name} secret",
          not (isinstance(d, dict) and ("access_token" in d or "token" in d)), {"status": s})

for key_id, _ in list(created.values()) + [(full_id, None), (exp_id, None)]:
    call("POST", f"/api/v1/admin/api-keys/{key_id}/revoke", headers={"X-API-Key": ADMIN}, label="cleanup-revoke")

receipt["summary"] = {"checks": len(receipt["checks"]), "failed": failures, "requests": len(receipt["requests"])}
json.dump(receipt, open("/work/receipt-client-credentials.json", "w"), indent=1)
print(json.dumps(receipt["summary"], indent=1))
print(json.dumps(receipt.get("grants"), indent=1))
for c in receipt["checks"]:
    print(("PASS " if c["pass"] else "FAIL ") + c["check"], json.dumps(c["detail"]))
sys.exit(1 if failures else 0)
