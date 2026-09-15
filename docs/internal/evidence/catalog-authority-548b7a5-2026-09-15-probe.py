"""Per-principal catalog visibility vs service/task access probe for honua-server#4783.

For every principal (bootstrap admin password, admin API key, role-matched scoped key, a
second scoped key whose permission names another service, a non-matching administrative
key, anonymous) this reads the REST catalog and the SOAP GetServiceDescriptions catalog,
then opens every FeatureServer, MapServer and GPServer service, layer, operation and task
route on the three authz fixture services.

A cell passes when:
  * REST and SOAP catalogs list the same (name, type, RestUrl) entries;
  * the visible fixture services equal the expectation derived from the seeded policy and
    the role each key authenticates with (not from observed output);
  * a listed service answers every route with a non-error JSON document, and an unlisted
    service refuses every route (403 authenticated / 499 anonymous) without layer content;
  * GP submitJob (execute authority, separate from catalog metadata) admits only the
    full-admin principals, with a jobId.
"""
import base64
import json
import os
import struct
import sys
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
from datetime import datetime, timezone

ORIGIN = os.environ["REPLAY_ORIGIN"]
CONNECT = os.environ["REPLAY_CONNECT"]
SECRETS = {"bootstrap": os.environ["REPLAY_BOOTSTRAP_KEY"]}

FIXTURE = {
    # service -> (layer id, seeded layer name)
    "arcgis_compat_protected": (2010, "Protected Points"),
    "arcgis_compat_scoped": (2020, "Scoped Points"),
    "browser_compat": (2000, None),
}

# Expected visibility, from docker/seed/arcgis-compat.sql policies and the roles the pinned
# ApiKeyAuthenticationHandler assigns to the keys minted below:
#   arcgis_compat_protected allowedRoles=[admin]; arcgis_compat_scoped allowedRoles=[scoped-api-key];
#   browser_compat allowAnonymous.
#   admin:*               -> role admin (full admin grant)
#   read:<service>        -> role scoped-api-key, whatever service the permission names
#   admin:read            -> role scoped-admin-key (administrative but not full admin), in
#                            neither restricted policy
EXPECTED_VISIBLE = {
    "bootstrap": {"arcgis_compat_protected", "arcgis_compat_scoped", "browser_compat"},
    "admin-key": {"arcgis_compat_protected", "arcgis_compat_scoped", "browser_compat"},
    "role-matched-key": {"arcgis_compat_scoped", "browser_compat"},
    "second-scoped-key": {"arcgis_compat_scoped", "browser_compat"},
    "non-matching-key": {"browser_compat"},
    "anonymous": {"browser_compat"},
}
KEY_GRANTS = {
    "admin-key": ["admin:*"],
    "role-matched-key": ["read:arcgis_compat_scoped"],
    "second-scoped-key": ["read:browser_compat"],
    "non-matching-key": ["admin:read"],
}

EXECUTE_ADMITTED = {"bootstrap", "admin-key"}

SOAP_CATALOG = (
    b'<soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"><soap:Body>'
    b'<GetServiceDescriptions xmlns="http://www.esri.com/schemas/ArcGIS/10.8" />'
    b"</soap:Body></soap:Envelope>"
)
# geometry.buffer task parameters on the pin: wkb (GPString, base64 OGC WKB), srid (GPLong),
# distance (GPDouble). The geometry is POINT(-122.5 37.5).
BUFFER_FORM = {
    "wkb": base64.b64encode(struct.pack("<BIdd", 1, 1, -122.5, 37.5)).decode("ascii"),
    "srid": "4326",
    "distance": "10",
    "f": "json",
}


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


OPENER = urllib.request.build_opener(NoRedirect)


def request(path, principal, method="GET", body=None, content_type=None):
    parts = urllib.parse.urlsplit(urllib.parse.urljoin(ORIGIN, path))
    assert parts.netloc == urllib.parse.urlsplit(ORIGIN).netloc, "never send credentials cross-origin"
    target = f"http://{CONNECT}{parts.path}" + (f"?{parts.query}" if parts.query else "")
    headers = {"Host": parts.hostname}
    if content_type:
        headers["Content-Type"] = content_type
    if principal != "anonymous":
        headers["X-API-Key"] = SECRETS[principal]
    req = urllib.request.Request(target, method=method, data=body, headers=headers)
    try:
        response = OPENER.open(req, timeout=60)
    except urllib.error.HTTPError as exc:
        response = exc
    with response:
        return response.status, response.read()


def outcome(status, raw):
    """0 for a successful non-error JSON document, else the Esri error code or HTTP status."""
    if status != 200:
        return status
    try:
        body = json.loads(raw)
    except ValueError:
        return -1
    if isinstance(body, dict) and "error" in body:
        code = body["error"].get("code")
        return code if isinstance(code, int) else -1
    return 0


def routes(service, layer):
    root = f"/rest/services/{service}"
    q = urllib.parse.quote
    return [
        ("FeatureServer", "GET", f"{root}/FeatureServer?f=json", None),
        ("FeatureServer", "GET", f"{root}/FeatureServer/{layer}?f=json", None),
        ("FeatureServer", "GET", f"{root}/FeatureServer/{layer}/query?where=1%3D1&returnGeometry=false&f=json", None),
        ("FeatureServer", "GET", f"{root}/FeatureServer/query?layerDefs={q(json.dumps({str(layer): '1=1'}))}&returnGeometry=false&f=json", None),
        ("FeatureServer", "GET", f"{root}/FeatureServer/queryDomains?layers={layer}&f=json", None),
        ("MapServer", "GET", f"{root}/MapServer?f=json", None),
        ("MapServer", "GET", f"{root}/MapServer/{layer}?f=json", None),
        ("MapServer", "GET", f"{root}/MapServer/export?bbox=-180,-90,180,90&size=256,256&f=json", None),
        ("MapServer", "GET", f"{root}/MapServer/identify?geometry=-122.5,37.5&geometryType=esriGeometryPoint&mapExtent=-180,-90,180,90&imageDisplay=800,600,96&layers=all&tolerance=2&f=json", None),
        ("MapServer", "GET", f"{root}/MapServer/find?searchText=a&layers={layer}&f=json", None),
        ("MapServer", "GET", f"{root}/MapServer/legend?f=json", None),
        ("MapServer", "GET", f"{root}/MapServer/layers?f=json", None),
        ("MapServer", "GET", f"{root}/MapServer/queryDomains?layers={layer}&f=json", None),
        ("MapServer", "GET", f"{root}/MapServer/{layer}/query?where=1%3D1&returnGeometry=false&f=json", None),
        ("GPServer", "GET", f"{root}/GPServer?f=json", None),
        ("GPServer", "GET", f"{root}/GPServer/geometry.buffer?f=json", None),
        ("GPServer", "GET", f"{root}/GPServer/Buffer?f=json", None),
    ]


def execute_route(service):
    return f"/rest/services/{service}/GPServer/geometry.buffer/submitJob", urllib.parse.urlencode(BUFFER_FORM).encode()


def main():
    out = sys.argv[1]
    assert os.environ["REPLAY_IMAGE_REVISION"] == os.environ["REPLAY_EXPECTED_REVISION"], "image revision mismatch"
    assert os.environ["REPLAY_IMAGE_REF"].endswith(os.environ["REPLAY_IMAGE_ID"]), "image digest mismatch"
    rows, owned, cleanup, error = [], [], [], None

    def check(name, passed, **details):
        rows.append({"check": name, "passed": bool(passed), **details})

    try:
        for principal, grants in KEY_GRANTS.items():
            status, raw = request("/api/v1/admin/api-keys/", "bootstrap", "POST",
                                  json.dumps({"name": f"h4783-replay-{principal}", "permissions": grants}).encode(),
                                  "application/json")
            assert status == 201, f"key mint for {principal} returned {status}"
            data = json.loads(raw)["data"]
            owned.append({"principal": principal, "id": data["apiKey"]["id"], "permissions": grants})
            SECRETS[principal] = data["key"]

        for principal, expected in EXPECTED_VISIBLE.items():
            status, raw = request("/rest/services?f=json", principal)
            rest = json.loads(raw).get("services", []) if status == 200 else []
            status_soap, raw_soap = request("/services", principal, "POST", SOAP_CATALOG, "text/xml")
            soap = [
                {child.tag.rsplit("}", 1)[-1]: child.text for child in element}
                for element in ET.fromstring(raw_soap).iter()
                if element.tag.rsplit("}", 1)[-1] == "ServiceDescription"
            ] if status_soap == 200 else []
            rest_entries = sorted((e["name"], e["type"], e["url"]) for e in rest)
            soap_entries = sorted((e.get("Name"), e.get("Type"), e.get("RestUrl")) for e in soap)
            check("rest-soap-catalog-parity", status == 200 and status_soap == 200 and rest_entries == soap_entries,
                  principal=principal, rest_status=status, soap_status=status_soap, entries=len(rest_entries))

            listed = {name for name, kind, _ in rest_entries if name in FIXTURE}
            check("catalog-visibility-matches-policy", listed == expected, principal=principal,
                  listed=sorted(listed), expected=sorted(expected))
            for name in FIXTURE:
                kinds = sorted(kind for n, kind, _ in rest_entries if n == name)
                if name in listed:
                    check("listed-service-kinds", {"FeatureServer", "MapServer", "GPServer"} <= set(kinds),
                          principal=principal, service=name, kinds=kinds)

            for service, (layer, layer_name) in FIXTURE.items():
                visible = service in listed
                for surface, method, path, body in routes(service, layer):
                    content_type = "application/x-www-form-urlencoded" if body else None
                    status, raw = request(path, principal, method, body, content_type)
                    result = outcome(status, raw)
                    if visible:
                        passed = result == 0
                        if passed and path.endswith("/submitJob"):
                            passed = "jobId" in json.loads(raw)
                    else:
                        denial = 499 if principal == "anonymous" else 403
                        passed = result == denial and (layer_name is None or layer_name.encode() not in raw)
                    check("listed-opens-unlisted-refuses", passed, principal=principal, service=service,
                          surface=surface, method=method, route=path.split("?", 1)[0], listed=visible,
                          http_status=status, outcome=result)

                # Execution is OperatorOperation.Execute, a separate authority from the metadata
                # the catalog hands off: only a full-admin principal holds it here. Read-only keys
                # and read-role policies do not confer it, and anonymous callers are challenged.
                path, body = execute_route(service)
                status, raw = request(path, principal, "POST", body, "application/x-www-form-urlencoded")
                result = outcome(status, raw)
                if principal in EXECUTE_ADMITTED:
                    passed = result == 0 and "jobId" in json.loads(raw)
                else:
                    passed = result == (499 if principal == "anonymous" else 403)
                detail = {}
                if result not in (0, 403, 499):
                    try:
                        error_body = json.loads(raw).get("error", {})
                        detail = {"message": error_body.get("message"), "details": error_body.get("details")}
                    except ValueError:
                        detail = {"body": raw[:300].decode("utf-8", "replace")}
                check("execute-authority", passed, principal=principal, service=service, route=path,
                      listed=visible, http_status=status, outcome=result, **detail)
    except Exception as exc:  # retained in the transcript, then re-raised by the final assert
        error = f"{type(exc).__name__}: {exc}"
    finally:
        for item in owned:
            status, raw = request(f"/api/v1/admin/api-keys/{item['id']}/revoke", "bootstrap", "POST", b"{}",
                                  "application/json")
            revoked = status == 200 and json.loads(raw).get("data", {}).get("status") == "revoked"
            # Restricted service: an anonymous-readable one would answer a revoked key too.
            after = outcome(*request("/rest/services/arcgis_compat_scoped/FeatureServer?f=json", item["principal"]))
            cleanup.append({**{k: v for k, v in item.items() if k != "id"}, "revoked": revoked,
                            "post_revoke_outcome": after, "post_revoke_refused": after in (401, 403, 498, 499)})

    transcript = {
        "issue": "honua-io/honua-server#4783",
        "completed_at": datetime.now(timezone.utc).isoformat(),
        "image": os.environ["REPLAY_IMAGE_REF"],
        "image_id": os.environ["REPLAY_IMAGE_ID"],
        "revision": os.environ["REPLAY_IMAGE_REVISION"],
        "fixture": {"repo": "honua-io/honua-esri-compat", "ref": os.environ["REPLAY_COMPAT_REF"],
                    "file": "docker/seed/arcgis-compat.sql", "sha256": os.environ["REPLAY_SEED_SHA256"]},
        "principals": {p: KEY_GRANTS.get(p, "admin password" if p == "bootstrap" else "none")
                       for p in EXPECTED_VISIBLE},
        "passed": sum(r["passed"] for r in rows),
        "failed": sum(not r["passed"] for r in rows),
        "error": error,
        "credential_cleanup": cleanup,
        "rows": rows,
    }
    text = json.dumps(transcript, indent=2) + "\n"
    for secret in SECRETS.values():
        assert secret not in text, "transcript must not carry credentials"
    with open(out, "w", encoding="utf-8") as handle:
        handle.write(text)
    print(json.dumps({k: transcript[k] for k in ("passed", "failed", "error")}))
    for row in rows:
        if not row["passed"]:
            print("FAIL", json.dumps(row))
    assert error is None and transcript["failed"] == 0
    assert all(c["revoked"] and c["post_revoke_refused"] for c in cleanup)


if __name__ == "__main__":
    main()
