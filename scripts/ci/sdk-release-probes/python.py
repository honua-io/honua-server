#!/usr/bin/env python3
import json
import os
import sys
import traceback
from datetime import datetime, timezone

from honua_sdk import HonuaClient

now = lambda: datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
base_url = os.environ.get("HONUA_SERVER_BASE_URL", "http://localhost:5000")
service_id = os.environ.get("HONUA_SDK_SERVICE_ID", "test_service")
client = HonuaClient(base_url)

def require_payload(value, *needles):
    payload = json.dumps(value, default=lambda item: getattr(item, "__dict__", str(item)))
    if value is None or payload in ("{}", "[]") or any(needle not in payload for needle in needles):
        raise AssertionError(f"SDK response failed invariant ({', '.join(needles)}): {payload[:1024]}")

def checked(invoke, *needles):
    return require_payload(invoke(), *needles)

cases = [
    ("serve.geoservices-root", "HonuaClient.list_services", lambda: checked(client.list_services, service_id)),
    ("serve.geoservices-featureserver", "FeatureServer.metadata", lambda: checked(client.feature_server(service_id).metadata, "Test Feature Service", "layers")),
    ("serve.geoservices-featureserver", "FeatureServer.query", lambda: require_payload(client.feature_server(service_id).query(0, where="1=1", extra_params={"resultRecordCount": 1}), "alpha")),
    ("serve.ogc-api-features", "OgcFeatures.landing", lambda: checked(client.ogc_features().landing, "links")),
    ("serve.ogc-api-features", "OgcFeatures.collections", lambda: checked(client.ogc_features().collections, service_id)),
    ("serve.stac", "StacClient.catalog", lambda: checked(client.stac().catalog, "links")),
    ("serve.odata", "ODataClient.layers", lambda: require_payload(client.odata().layers(top=1), "Test Layer")),
]
observations = []
try:
    for capability, operation, invoke in cases:
        started_at = now()
        try:
            invoke()
            observations.append({"capability": capability, "operation": operation, "result": "pass", "startedAt": started_at, "completedAt": now()})
        except Exception:
            observations.append({"capability": capability, "operation": operation, "result": "fail", "startedAt": started_at, "completedAt": now(), "trace": traceback.format_exc(limit=12)[-8192:]})
finally:
    client.close()
with open(sys.argv[1], "w", encoding="utf-8") as handle:
    json.dump({"observations": observations}, handle, indent=2)
    handle.write("\n")
raise SystemExit(any(item["result"] != "pass" for item in observations))
