"""#4577 probe: does an exchange turn a managed key's permission label into a role?

Layer 2030 (arcgis_compat_role) admits only role "field-editor". A key whose
permission label is "field-editor" is a "scoped-api-key" principal on the direct
X-API-Key transport. Compare direct access with both exchange paths.
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
REFERER = "https://desktop.honua.test"
receipt = {"requests": [], "observations": []}


def call(method, path, *, headers=None, form=None, body=None, label):
    data, hdrs = None, dict(headers or {})
    if form is not None:
        data = urllib.parse.urlencode(form).encode()
        hdrs["Content-Type"] = "application/x-www-form-urlencoded"
    elif body is not None:
        data = json.dumps(body).encode()
        hdrs["Content-Type"] = "application/json"
    try:
        with urllib.request.urlopen(urllib.request.Request(BASE + path, data=data, method=method, headers=hdrs),
                                    context=CTX, timeout=30) as r:
            status, raw = r.status, r.read()
    except urllib.error.HTTPError as e:
        status, raw = e.code, e.read()
    try:
        doc = json.loads(raw) if raw else None
    except ValueError:
        doc = None
    err = None
    if isinstance(doc, dict):
        err = doc.get("error") if isinstance(doc.get("error"), str) else (doc.get("error") or {}).get("code")
    receipt["requests"].append({"label": label, "method": method, "path": path.split("?")[0], "status": status,
                                "error": err, "tokenIssued": isinstance(doc, dict) and ("token" in doc or "access_token" in doc),
                                "bodySha256": hashlib.sha256(raw).hexdigest()})
    return status, doc, err


def names(doc):
    if not isinstance(doc, dict) or "features" not in doc:
        return []
    return sorted(n for n in ((f.get("attributes") or f.get("properties") or {}).get("name") for f in doc["features"]) if n)


def read_2030(label, headers=None, token_query=None):
    q = "&token=" + urllib.parse.quote(token_query) if token_query else ""
    fs = call("GET", "/rest/services/arcgis_compat_role/FeatureServer/2030/query?where=1%3D1&outFields=*&f=json" + q,
              headers=headers, label=label + "-fs")
    ogc = call("GET", "/ogc/features/collections/2030/items?f=json", headers=headers, label=label + "-ogc")
    return {"fs": {"status": fs[0], "error": fs[2], "features": names(fs[1])},
            "ogc": {"status": ogc[0], "error": ogc[2], "features": names(ogc[1])}}


def observe(name, result):
    receipt["observations"].append({"credential": name, **result})
    print(name, json.dumps(result))


exp = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(time.time() + 600))
keys = {}
for name, perms in (("label-field-editor", ["field-editor"]), ("label-read-scoped", ["read:arcgis_compat_scoped"])):
    s, d, _ = call("POST", "/api/v1/admin/api-keys", headers={"X-API-Key": ADMIN},
                   body={"name": name, "permissions": perms, "expiresAt": exp}, label="create-" + name)
    keys[name] = (d["data"]["apiKey"]["id"], d["data"]["key"])

observe("anonymous", read_2030("anonymous"))
observe("bootstrap-admin X-API-Key", read_2030("admin-direct", headers={"X-API-Key": ADMIN}))

for name, (_, material) in keys.items():
    observe(f"{name} direct X-API-Key", read_2030(name + "-direct", headers={"X-API-Key": material}))

    s, d, err = call("POST", "/sharing/rest/oauth2/token", label=name + "-cc-grant",
                     form={"grant_type": "client_credentials", "client_id": name, "client_secret": material})
    token = (d or {}).get("access_token") if isinstance(d, dict) else None
    if token:
        r = read_2030(name + "-cc-bearer", headers={"Authorization": "Bearer " + token})
        r_q = read_2030(name + "-cc-query", token_query=token)
        observe(f"{name} client_credentials token (Bearer)", r)
        observe(f"{name} client_credentials token (?token= on FS)", {"fs": r_q["fs"]})
    else:
        observe(f"{name} client_credentials grant", {"status": s, "error": err, "issued": False})

    s, d, err = call("POST", "/sharing/rest/generateToken", label=name + "-generateToken",
                     headers={"Referer": REFERER},
                     form={"username": "desktop-user", "password": material, "client": "referer", "referer": REFERER, "f": "json"})
    observe(f"{name} generateToken bridge", {"status": s, "error": err, "issued": isinstance(d, dict) and "token" in d})

for key_id, _ in keys.values():
    call("POST", f"/api/v1/admin/api-keys/{key_id}/revoke", headers={"X-API-Key": ADMIN}, label="cleanup-revoke")
json.dump(receipt, open("/work/receipt-role-label.json", "w"), indent=1)
