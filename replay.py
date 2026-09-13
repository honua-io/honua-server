"""#4577 replay against the pinned candidate over verified HTTPS.

Runs inside a container on the candidate network. Secrets (admin password,
key material, issued tokens) never enter the receipt.
"""
import hashlib
import json
import os
import ssl
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

BASE = "https://candidate.honua.test:8443"
REFERER = "https://desktop.honua.test"
CTX = ssl.create_default_context(cafile="/work/root.crt")
ADMIN = open("/work/admin.pw").read().strip()
PROTECTED, SCOPED = "arcgis_compat_protected", "arcgis_compat_scoped"

receipt = {"requests": [], "checks": []}
failures = []


def call(method, path, *, headers=None, form=None, body=None, label):
    data = None
    hdrs = dict(headers or {})
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
    shown = path.split("?")[0] + ("?" + "&".join(
        f"{k}={'<redacted>' if k == 'token' else v}"
        for k, v in urllib.parse.parse_qsl(path.split("?", 1)[1])) if "?" in path else "")
    receipt["requests"].append({
        "label": label, "method": method, "path": shown, "status": status,
        "esriError": (doc or {}).get("error", {}).get("code") if isinstance(doc, dict) else None,
        "tokenIssued": isinstance(doc, dict) and "token" in doc,
        "bodySha256": hashlib.sha256(raw).hexdigest(),
    })
    return status, doc


def check(name, ok, detail):
    receipt["checks"].append({"check": name, "pass": bool(ok), "detail": detail})
    if not ok:
        failures.append(name)


def admin_headers():
    return {"X-API-Key": ADMIN}


def feature_names(doc):
    if not isinstance(doc, dict):
        return []
    if "features" in doc:
        out = []
        for f in doc["features"]:
            props = f.get("attributes") or f.get("properties") or {}
            out.append(props.get("name"))
        return sorted(n for n in out if n)
    return []


def denied(status, doc):
    code = (doc or {}).get("error", {}).get("code") if isinstance(doc, dict) else None
    return (status in (401, 403) or code in (401, 403, 498, 499)) and not feature_names(doc)


# ---- discovery ------------------------------------------------------------
# Fixture ids (fixture.sql): FeatureServer layer ids and OGC collection ids
# are the layer ids. The scoped service admits only role "scoped-api-key",
# so the admin credential cannot discover it.
layer = {PROTECTED: 2010, SCOPED: 2020}
collection = {PROTECTED: "2010", SCOPED: "2020"}
s, d = call("GET", f"/rest/services/{PROTECTED}/FeatureServer?f=json", headers=admin_headers(), label="discover-fs-protected")
check("admin discovers protected layer 2010", s == 200 and [l.get("id") for l in d.get("layers", [])] == [2010],
      {"status": s})
receipt["discovered"] = {"featureServerLayer": layer, "ogcCollection": collection}


def fs_read(svc, headers=None, token=None, label=""):
    q = {"where": "1=1", "outFields": "*", "f": "json"}
    if token:
        q["token"] = token
    return call("GET", f"/rest/services/{svc}/FeatureServer/{layer[svc]}/query?" + urllib.parse.urlencode(q),
                headers=headers, label=label)


def ogc_read(svc, headers=None, token=None, label=""):
    # OGC API rejects unknown query parameters (400 "Unknown query parameter:
    # token"), so the portal token travels in the Authorization header.
    headers = dict(headers or {})
    if token:
        headers["Authorization"] = "Bearer " + token
    return call("GET", f"/ogc/features/collections/{collection[svc]}/items?f=json",
                headers=headers, label=label)


def reads(prefix, headers=None, token=None):
    return {
        (proto, svc): (fs_read if proto == "fs" else ogc_read)(svc, headers=headers, token=token,
                                                                label=f"{prefix}-{proto}-{svc}")
        for proto in ("fs", "ogc") for svc in (PROTECTED, SCOPED)
    }


# ---- anonymous controls ---------------------------------------------------
for (proto, svc), (s, d) in reads("anonymous").items():
    check(f"anonymous {proto} {svc} denied", denied(s, d), {"status": s})

# ---- managed keys ---------------------------------------------------------
def create_key(name, permissions, expires_in=600):
    exp = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(time.time() + expires_in))
    s, d = call("POST", "/api/v1/admin/api-keys", headers=admin_headers(),
                body={"name": name, "permissions": permissions, "expiresAt": exp}, label=f"create-key-{name}")
    assert s in (200, 201), (name, s)
    return d["data"]["apiKey"]["id"], d["data"]["key"]


constrained = {
    "scoped-read": [f"read:{SCOPED}"],
    "scoped-write": [f"write:{SCOPED}/{layer[SCOPED]}"],
    "admin-read": ["admin:read"],
    "admin-read-approve": ["admin:read", "admin:approve"],
    "ops-read": ["ops:read"],
    "unknown": ["unrecognized:permission"],
}
keys = {n: create_key(n, p) for n, p in constrained.items()}
full_admin_id, full_admin_key = create_key("full-admin", ["admin:*"])
revoked_id, revoked_key = create_key("revoked-admin", ["admin:*"])
call("POST", f"/api/v1/admin/api-keys/{revoked_id}/revoke", headers=admin_headers(), label="revoke-key")
expiring_id, expiring_key = create_key("expiring-admin", ["admin:*"], expires_in=8)

# direct scoped-key transport keeps its scope
direct = reads("scoped-key-direct", headers={"X-API-Key": keys["scoped-read"][1]})
for proto in ("fs", "ogc"):
    s, d = direct[(proto, SCOPED)]
    check(f"scoped key direct {proto} reads allowed service", s == 200 and feature_names(d) == ["scoped-alpha"],
          {"status": s, "features": feature_names(d)})
    s, d = direct[(proto, PROTECTED)]
    check(f"scoped key direct {proto} denied protected service", denied(s, d), {"status": s})


def exchange(username, password, client, label):
    form = {"username": username, "password": password, "client": client, "f": "json", "expiration": "5"}
    headers = {}
    if client == "referer":
        form["referer"] = REFERER
        headers["Referer"] = REFERER
    s, d = call("POST", "/sharing/rest/generateToken", form=form, headers=headers, label=label)
    return s, d, headers


def refused(d):
    return isinstance(d, dict) and "token" not in d and d.get("error", {}).get("code") == 400


# ---- exchange of constrained keys is refused -------------------------------
for name, (_, material) in keys.items():
    for username in ("admin", "desktop-user"):
        for client in ("requestip", "referer"):
            s, d, _ = exchange(username, material, client, f"exchange-{name}-{username}-{client}")
            check(f"exchange refused: {name} as {username} ({client})", refused(d),
                  {"status": s, "esriError": (d or {}).get("error", {}).get("code"), "tokenIssued": "token" in (d or {})})

# ---- supported exchanges still issue working admin tokens -----------------
supported = [("bootstrap-password", "admin", ADMIN),
             ("full-admin-key", "admin", full_admin_key),
             ("full-admin-key", "desktop-user", full_admin_key)]
for name, username, password in supported:
    for client in ("requestip", "referer"):
        s, d, headers = exchange(username, password, client, f"exchange-{name}-{username}-{client}")
        token = (d or {}).get("token")
        check(f"exchange issued: {name} as {username} ({client})", s == 200 and bool(token), {"status": s})
        if token:
            for (proto, svc), (rs, rd) in reads(f"token-{name}-{username}-{client}", headers=headers, token=token).items():
                # Admin authority reads both features, as the bootstrap-admin
                # row of the SERVER-015 diagnostic did.
                expected = ["protected-alpha"] if svc == PROTECTED else ["scoped-alpha"]
                check(f"{name} token ({username}, {client}) {proto} reads {svc}",
                      rs == 200 and feature_names(rd) == expected, {"status": rs, "features": feature_names(rd)})

# ---- invalid / revoked / expired / wrong password ------------------------
time.sleep(10)
negatives = [("revoked-key", revoked_key), ("expired-key", expiring_key),
             ("invalid-credential", "hnua_notarealkey0000000000000000"), ("wrong-password", ADMIN + "x")]
for name, password in negatives:
    for username in ("admin", "desktop-user"):
        s, d, _ = exchange(username, password, "requestip", f"exchange-{name}-{username}")
        check(f"exchange rejected: {name} as {username}",
              isinstance(d, dict) and "token" not in d and "error" in d, {"status": s, "esriError": (d or {}).get("error", {}).get("code")})

# ---- cleanup --------------------------------------------------------------
for key_id, _ in list(keys.values()) + [(full_admin_id, None), (expiring_id, None)]:
    call("POST", f"/api/v1/admin/api-keys/{key_id}/revoke", headers=admin_headers(), label="cleanup-revoke")

receipt["summary"] = {"checks": len(receipt["checks"]), "failed": failures, "requests": len(receipt["requests"])}
json.dump(receipt, open("/work/receipt.json", "w"), indent=1)
print(json.dumps(receipt["summary"], indent=1))
for c in receipt["checks"]:
    print(("PASS " if c["pass"] else "FAIL ") + c["check"], json.dumps(c["detail"]))
sys.exit(1 if failures else 0)
