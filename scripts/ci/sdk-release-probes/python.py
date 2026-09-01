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
cases = [
    ("serve.geoservices-root", "HonuaClient.list_services", lambda: client.list_services()),
    ("serve.geoservices-featureserver", "FeatureServer.metadata", lambda: client.feature_server(service_id).metadata()),
    ("serve.geoservices-featureserver", "FeatureServer.query", lambda: client.feature_server(service_id).query(0, where="1=1", extra_params={"resultRecordCount": 1})),
    ("serve.ogc-api-features", "OgcFeatures.landing", lambda: client.ogc_features().landing()),
    ("serve.ogc-api-features", "OgcFeatures.collections", lambda: client.ogc_features().collections()),
    ("serve.stac", "StacClient.catalog", lambda: client.stac().catalog()),
    ("serve.odata", "ODataClient.layers", lambda: client.odata().layers(top=1)),
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
