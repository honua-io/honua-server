#!/usr/bin/env python3
"""Exercise the exact canary workloads on an ephemeral published server image.

This loopback HTTP/Development/API-key rehearsal is not scheduled candidate proof.
"""
import os
import urllib.request
from pathlib import Path

from gp_canary import Client, NoRedirect, PROCESSES, now, run_operation, write_json


class RehearsalApiKey(urllib.request.BaseHandler):
    def http_request(self, request):
        request.remove_header("Authorization")
        request.add_header("X-API-Key", "gp-reliability-admin")
        return request


def main():
    folder = Path("artifacts/gp-canary-rehearsal")
    receipt = {"schema": "honua.gp-canary-rehearsal.v1", "qualification": False,
               "started_at": now(), "outcome": "fail", "operations": [],
               "server_image": os.environ["HONUA_SERVER_IMAGE"],
               "source_sha": os.environ["HONUA_REHEARSAL_SOURCE_SHA"]}
    try:
        client = Client("http://127.0.0.1:18080", "unused")
        client.opener = urllib.request.build_opener(NoRedirect, RehearsalApiKey)
        pin = {"server_digest": receipt["server_image"].split("@", 1)[1]}
        receipt["identity_before"] = client.identity(pin)
        for process in PROCESSES:
            run_operation(client, process, folder, receipt)
        receipt["identity_after"] = client.identity(pin)
        if not all(operation["outcome"] == "pass" for operation in receipt["operations"]):
            raise ValueError("published-image API-shape/numerical rehearsal failed")
        receipt["outcome"] = "pass"
    except Exception as error:
        receipt["finding"] = str(error)
    finally:
        receipt["completed_at"] = now()
        write_json(folder / "rehearsal.json", receipt)
    return 0 if receipt["outcome"] == "pass" else 1


if __name__ == "__main__":
    raise SystemExit(main())
