# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Bootstrap PostGIS + Honua server for JS integration tests.

This script starts a PostGIS container, seeds a test catalog, launches the
Honua server, prints connection details as JSON, and waits until terminated.
"""

from __future__ import annotations

import json
import os
import signal
import sys
import time
from pathlib import Path


def _ensure_import_path() -> None:
    tests_python_root = Path(__file__).resolve().parents[1]
    sys.path.insert(0, str(tests_python_root))


def main() -> None:
    _ensure_import_path()

    from shared.postgis import PostGISFixture
    from shared.server import HonuaServer

    service_id = os.getenv("HONUA_SERVICE_ID", "test_service_gw0")
    layer_id = int(os.getenv("HONUA_LAYER_ID", "1000"))
    port = int(os.getenv("HONUA_TEST_PORT", "5555"))
    timeout = float(os.getenv("HONUA_TEST_TIMEOUT", "120"))

    postgis = PostGISFixture()
    postgis.start()

    schema = postgis.create_isolated_schema("js")
    postgis.seed_test_catalog(service_name=service_id, layer_id=layer_id, schema=schema)
    postgis.seed_test_features(schema=schema, layer_id=layer_id)

    connection_string = postgis.get_npgsql_connection_string(search_path=f"{schema}, public")
    server = HonuaServer(connection_string=connection_string, port=port)
    server.start(timeout=timeout)

    payload = {
        "base_url": server.base_url,
        "service_id": service_id,
        "layer_id": layer_id,
    }
    print(json.dumps(payload), flush=True)

    def shutdown(*_args) -> None:
        server.stop()
        postgis.drop_schema(schema)
        postgis.stop()
        sys.exit(0)

    signal.signal(signal.SIGTERM, shutdown)
    signal.signal(signal.SIGINT, shutdown)

    while True:
        time.sleep(1)


if __name__ == "__main__":
    main()
