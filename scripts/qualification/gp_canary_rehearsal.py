#!/usr/bin/env python3
"""Exercise the exact canary workloads on an ephemeral published server image.

This loopback HTTP/Development/API-key rehearsal is not scheduled candidate proof.
"""
import os
import json
import subprocess
import sys
import urllib.request
from pathlib import Path

from gp_canary import Client, NoRedirect, PROCESSES, now, run_operation, write_json


class RehearsalApiKey(urllib.request.BaseHandler):
    def http_request(self, request):
        request.remove_header("Authorization")
        request.add_header("X-API-Key", "gp-reliability-admin")
        return request


class RehearsalClient(Client):
    def __init__(self, folder):
        super().__init__("http://127.0.0.1:18080", "unused")
        self.opener = urllib.request.build_opener(NoRedirect, RehearsalApiKey)
        self.folder = folder
        self.result_documents = 0

    def request(self, path, body=None, expected=200):
        result = super().request(path, body, expected)
        if path.endswith("/results"):
            # OGC value transmission materializes these managed geometry results,
            # even when the host has an attested output store. Retain the wire
            # document before the shared decoder/oracle validates its contents.
            self.result_documents += 1
            write_json(self.folder / f"result-document-{self.result_documents}.json", result)
        return result


def provision_store():
    """Use the resolved Compose contract and existing local-volume provisioner."""
    root = Path(os.environ["HONUA_GP_OBJECT_ROOT"]).resolve()
    root.mkdir(parents=True, exist_ok=True)
    root.chmod(0o777)
    configuration = json.loads(subprocess.check_output(
        ["docker", "compose", "config", "--format", "json"], timeout=30))
    environment = configuration["services"]["server"]["environment"]
    prefix = "Geoprocessing__OutputStaging__"
    settings = {
        "store-reference": "StoreReference", "persistence-class": "PersistenceClass",
        "backup-identity": "BackupIdentity", "backup-store-references": "BackupStoreReferences__0",
        "key-prefix": "KeyPrefix", "max-inline-artifact-bytes": "MaxInlineArtifactBytes",
        "read-lease-duration": "ReadLeaseDuration", "sweep-interval": "SweepInterval",
        "sweep-grace": "SweepGrace", "orphan-retention": "OrphanRetention",
    }
    arguments = ["scripts/operations/initialize-gp-output-store.sh", "--root-path", str(root)]
    for option, setting in settings.items():
        arguments.extend(["--" + option, environment[prefix + setting]])
    digest = subprocess.check_output(arguments, text=True, timeout=30).strip()
    if digest != environment[prefix + "ConfigurationDigest"]:
        raise ValueError("rehearsal store contract digest does not match the resolved topology")
    (root / ".honua-gp-store.json").chmod(0o644)


def main():
    folder = Path("artifacts/gp-canary-rehearsal")
    receipt = {"schema": "honua.gp-canary-rehearsal.v1", "qualification": False,
               "started_at": now(), "outcome": "fail", "operations": [],
               "server_image": os.environ["HONUA_SERVER_IMAGE"],
               "source_sha": os.environ["HONUA_REHEARSAL_SOURCE_SHA"]}
    try:
        client = RehearsalClient(folder)
        pin = {"server_digest": receipt["server_image"].split("@", 1)[1]}
        receipt["identity_before"] = client.identity(pin)
        for process in PROCESSES:
            run_operation(client, process, folder, receipt)
        receipt["identity_after"] = client.identity(pin)
        receipt["result_document_count"] = client.result_documents
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
    if sys.argv[1:] == ["--provision-store"]:
        provision_store()
    else:
        raise SystemExit(main())
