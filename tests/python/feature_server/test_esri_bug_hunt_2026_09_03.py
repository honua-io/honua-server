# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""Independent Esri REST bug-hunt regressions against a live FeatureServer.

These tests intentionally assert the ArcGIS client contract.  They are kept
separate from the shared fixture because the hunt runs against a disposable
server/database selected with ``ESRI_PROBE_FEATURESERVER_URL``.
"""

from __future__ import annotations

import json
import os

import httpx
import pytest


DEFAULT_FEATURESERVER_URL = (
    "http://localhost:18080/rest/services/esri_probe_2026/FeatureServer"
)


def _feature_server_url() -> str:
    return os.environ.get("ESRI_PROBE_FEATURESERVER_URL", DEFAULT_FEATURESERVER_URL)


@pytest.mark.integration
@pytest.mark.featureserver
def test_arcgis_attribute_only_update_succeeds() -> None:
    """ArcGIS FeatureLayer.applyEdits updates attributes without resending geometry."""

    feature_server = _feature_server_url()
    layer_url = f"{feature_server}/2600"
    feature = {
        "geometry": {
            "x": -122.25,
            "y": 37.75,
            "spatialReference": {"wkid": 4326},
        },
        "attributes": {"name": "esri-attribute-update-before"},
    }

    with httpx.Client(timeout=30.0) as client:
        added = client.post(
            f"{layer_url}/applyEdits",
            data={"adds": json.dumps([feature]), "f": "json"},
        )
        assert added.status_code == 200, added.text
        object_id = added.json()["addResults"][0]["objectId"]

        try:
            updated = client.post(
                f"{layer_url}/applyEdits",
                data={
                    "updates": json.dumps(
                        [
                            {
                                "attributes": {
                                    "objectid": object_id,
                                    "name": "esri-attribute-update-after",
                                }
                            }
                        ]
                    ),
                    "f": "json",
                },
            )
            assert updated.status_code == 200, updated.text
            result = updated.json()["updateResults"][0]
            assert result["success"] is True, result
        finally:
            client.post(
                f"{layer_url}/applyEdits",
                data={"deletes": str(object_id), "f": "json"},
            )


@pytest.mark.integration
@pytest.mark.featureserver
def test_sync_operations_are_rejected_when_sync_is_disabled() -> None:
    """createReplica must not succeed when the FeatureServer says sync is disabled."""

    feature_server = _feature_server_url()
    with httpx.Client(timeout=30.0) as client:
        metadata = client.get(f"{feature_server}?f=json")
        assert metadata.status_code == 200, metadata.text
        assert metadata.json()["syncEnabled"] is False

        replica = client.post(
            f"{feature_server}/createReplica",
            data={
                "replicaName": "esri-sync-disabled-probe",
                "layers": "2600",
                "syncModel": "none",
                "dataFormat": "json",
                "returnAttachments": "false",
                "f": "json",
            },
        )
        assert replica.status_code == 200, replica.text
        replica_body = replica.json()
        try:
            assert isinstance(replica_body.get("error"), dict), replica_body
        finally:
            replica_id = replica_body.get("replicaID")
            if replica_id:
                client.post(
                    f"{feature_server}/unRegisterReplica",
                    data={"replicaID": replica_id, "f": "json"},
                )
