# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

"""
Bootstrap PostGIS + Honua server for JS integration tests.

This script starts a PostGIS container, seeds a test catalog, launches the
Honua server, prints connection details as JSON, and waits until terminated.
"""

from __future__ import annotations

from contextlib import ExitStack
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
    from shared.process_ports import server_port

    service_id = os.getenv("HONUA_SERVICE_ID", "test_service_gw0")
    layer_id = int(os.getenv("HONUA_LAYER_ID", "1000"))
    port = server_port("HONUA_TEST_PORT", HonuaServer.DEFAULT_PORT)
    timeout = float(os.getenv("HONUA_TEST_TIMEOUT", "120"))

    def shutdown(*_args) -> None:
        raise SystemExit(0)

    signal.signal(signal.SIGTERM, shutdown)
    signal.signal(signal.SIGINT, shutdown)

    with ExitStack() as cleanup:
        postgis = PostGISFixture()
        cleanup.callback(postgis.stop)
        postgis.start()
        schema = postgis.create_isolated_schema("js")
        cleanup.callback(postgis.drop_schema, schema)
        postgis.seed_test_catalog(service_name=service_id, layer_id=layer_id, schema=schema)
        postgis.seed_test_features(schema=schema, layer_id=layer_id)
        connection_string = postgis.get_npgsql_connection_string(search_path=f"{schema}, public")
        server = HonuaServer(connection_string=connection_string, port=port)
        cleanup.callback(server.stop)
        server.start(timeout=timeout)
        print(json.dumps({"base_url": server.base_url, "service_id": service_id, "layer_id": layer_id}), flush=True)
        while True:
            time.sleep(1)


if __name__ == "__main__":
    main()
