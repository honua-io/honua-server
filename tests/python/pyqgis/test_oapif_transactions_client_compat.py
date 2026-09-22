# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.
"""OGC API - Features Part 4 (Create, Replace, Update, Delete) through the real
QGIS OAPIF provider.

The provider reports the write surface per collection - ``capabilities()`` on
each scratch collection carries AddFeatures, ChangeAttributeValues and
DeleteFeatures - and issues POST / PATCH / DELETE against ``/items``.

Everything ``test_wfst_client_compat`` records about this fixture applies here
unchanged, because both providers write to the same three scratch collections:
writes require the ``X-API-Key`` header and reads do not; the provider sends
that header only from a QGIS authentication-database entry referenced as
``authcfg=<id>``, which the lane provisions in-process
(``conftest.api_header_authcfg``); collections 10, 11 and 12 are shared with
the OWSLib lane, so no case asserts an absolute count; and every verification
is an independent read of ``/items``, never the client's own report.
"""

from __future__ import annotations

import json
import urllib.parse
import urllib.request

import pytest

from .conftest import (
    CertificationEvidenceCollector,
    make_oapif_layer,
)

INSERT_COLLECTION = "10"
UPDATE_COLLECTION = "11"
DELETE_COLLECTION = "12"

#: Inside the browser_compat cluster, so an inserted point lands in the fixture
#: envelope rather than at (0, 0).
TEST_LON = -122.4188
TEST_LAT = 37.7742


def _read_names(base_url: str, collection_id: str) -> list[str]:
    """Names currently held by a scratch collection, read independently of QGIS."""
    url = (f"{base_url.rstrip('/')}/ogc/features/collections/"
           f"{collection_id}/items?" + urllib.parse.urlencode({
               "limit": 200, "f": "json"}))
    with urllib.request.urlopen(url, timeout=30) as response:
        features = json.load(response).get("features", [])
    return sorted(
        str((feature.get("properties") or {}).get("name")) for feature in features)


@pytest.mark.integration
@pytest.mark.pyqgis
class TestOapifTransactionClientCompat:
    """OGC API Features Part 4 through the QGIS OAPIF provider, verified server-side."""

    def _editable(self, base_url: str, collection_id: str, authcfg: str | None):
        from qgis.core import QgsVectorDataProvider

        if authcfg is None:
            pytest.skip(
                "HONUA_ADMIN_PASSWORD is unset, and an OGC API Features write is "
                "refused anonymously with 'Authentication is required to access "
                "this resource'; the write path cannot be exercised without it")

        layer = make_oapif_layer(
            base_url, collection_id, extra_params=f"authcfg='{authcfg}'")
        assert layer.isValid(), (
            f"collection {collection_id} did not load, so no write can be "
            f"attempted: {layer.error().summary()}"
        )
        capabilities = layer.dataProvider().capabilities()
        for required in ("AddFeatures", "DeleteFeatures", "ChangeAttributeValues"):
            bit = getattr(QgsVectorDataProvider, required)
            assert capabilities & bit, (
                f"collection {collection_id} does not advertise {required}, so the "
                "server's Part 4 conformance is not reaching the client")
        return layer

    def _add(self, layer, marker: str) -> bool:
        from qgis.core import QgsFeature, QgsGeometry, QgsPointXY

        feature = QgsFeature(layer.fields())
        feature.setAttribute("name", marker)
        feature.setGeometry(QgsGeometry.fromPointXY(QgsPointXY(TEST_LON, TEST_LAT)))
        assert layer.startEditing(), "the provider refused to begin an edit session"
        assert layer.addFeature(feature), "addFeature was rejected client-side"
        return layer.commitChanges()

    def _delete_marker(self, layer, base_url: str, collection_id: str, marker: str) -> bool:
        layer.reload()
        target = next(
            (f for f in layer.getFeatures() if f.attribute("name") == marker), None)
        if target is None:
            return marker not in _read_names(base_url, collection_id)
        assert layer.startEditing()
        assert layer.deleteFeature(target.id()), "deleteFeature was rejected client-side"
        return layer.commitChanges()

    # NB-PQG-OAPIFT-01 / transactions-part4 (create).
    def test_create_adds_a_feature_on_the_server(
        self, qgis_app, base_url: str,
        oapif_evidence: CertificationEvidenceCollector,
        api_header_authcfg: str | None,
    ) -> None:
        layer = self._editable(base_url, INSERT_COLLECTION, api_header_authcfg)
        before = _read_names(base_url, INSERT_COLLECTION)
        marker = "pyqgis-oapift-create"
        assert marker not in before, (
            f"{marker!r} is already present, so an earlier run did not clean up")

        committed = self._add(layer, marker)
        try:
            after = _read_names(base_url, INSERT_COLLECTION)
            assert marker in after, (
                f"commitChanges() returned {committed} but the server does not "
                f"hold {marker!r}; it holds {after}. {layer.commitErrors()}"
            )
            assert len(after) == len(before) + 1, (
                f"the create changed the row count by {len(after) - len(before)}, "
                "not 1")
            oapif_evidence.record(
                "NB-PQG-OAPIFT-01", "pass", measured_count=len(after),
                notes=(
                    f"OGC API Features Part 4 create through the QGIS OAPIF "
                    f"provider added {marker!r}, confirmed by an independent "
                    "/items read rather than by commitChanges(). Authenticated "
                    "with the X-API-Key header from the session authcfg; an "
                    "anonymous write is refused."
                ),
            )
        finally:
            restored = self._delete_marker(layer, base_url, INSERT_COLLECTION, marker)
            assert marker not in _read_names(base_url, INSERT_COLLECTION), (
                f"cleanup failed (commit returned {restored}); collection "
                f"{INSERT_COLLECTION} still holds {marker!r}, which would drift "
                "the next run"
            )

    # NB-PQG-OAPIFT-02 / transactions-part4 (update).
    def test_update_changes_an_attribute_on_the_server(
        self, qgis_app, base_url: str,
        oapif_evidence: CertificationEvidenceCollector,
        api_header_authcfg: str | None,
    ) -> None:
        layer = self._editable(base_url, UPDATE_COLLECTION, api_header_authcfg)
        before = _read_names(base_url, UPDATE_COLLECTION)
        marker = "pyqgis-oapift-update"
        assert marker not in before, (
            f"{marker!r} is already present, so an earlier run did not clean up")

        # Update a row this case owns, so a shared scratch row is never rewritten.
        seed_marker = "pyqgis-oapift-update-seed"
        assert self._add(layer, seed_marker), (
            f"seeding the update target failed: {layer.commitErrors()}")
        layer.reload()
        target = next(
            (f for f in layer.getFeatures() if f.attribute("name") == seed_marker),
            None)
        assert target is not None, "the seeded target is not visible to the provider"
        field = layer.fields().indexOf("name")
        assert field >= 0, "the scratch collection has no 'name' field to change"
        assert layer.startEditing()
        assert layer.changeAttributeValue(target.id(), field, marker), (
            "changeAttributeValue was rejected client-side")
        committed = layer.commitChanges()
        try:
            after = _read_names(base_url, UPDATE_COLLECTION)
            assert marker in after, (
                f"commitChanges() returned {committed} but the server still "
                f"holds {after}. {layer.commitErrors()}"
            )
            assert seed_marker not in after, (
                "the update inserted a new row instead of modifying the target")
            assert len(after) == len(before) + 1, (
                "the update changed the row count beyond the one row this case added")
            oapif_evidence.record(
                "NB-PQG-OAPIFT-02", "pass",
                notes=(
                    f"OGC API Features Part 4 update rewrote 'name' from "
                    f"{seed_marker!r} to {marker!r} in place, confirmed "
                    "server-side, with no extra row created."
                ),
            )
        finally:
            for leftover in (marker, seed_marker):
                self._delete_marker(layer, base_url, UPDATE_COLLECTION, leftover)
            remaining = _read_names(base_url, UPDATE_COLLECTION)
            assert marker not in remaining and seed_marker not in remaining, (
                f"cleanup failed; collection {UPDATE_COLLECTION} still holds {remaining}")

    # NB-PQG-OAPIFT-03 / transactions-part4 (delete).
    def test_delete_removes_a_feature_from_the_server(
        self, qgis_app, base_url: str,
        oapif_evidence: CertificationEvidenceCollector,
        api_header_authcfg: str | None,
    ) -> None:
        layer = self._editable(base_url, DELETE_COLLECTION, api_header_authcfg)
        before = _read_names(base_url, DELETE_COLLECTION)
        marker = "pyqgis-oapift-delete"
        assert marker not in before, (
            f"{marker!r} is already present, so an earlier run did not clean up")

        # Delete a row this case created, so the delete has an unambiguous target
        # and a shared scratch row is never destroyed.
        assert self._add(layer, marker), (
            f"seeding the delete target failed: {layer.commitErrors()}")
        assert marker in _read_names(base_url, DELETE_COLLECTION), (
            "the delete target was not created, so the delete would prove nothing")

        committed = self._delete_marker(layer, base_url, DELETE_COLLECTION, marker)
        after = _read_names(base_url, DELETE_COLLECTION)
        assert marker not in after, (
            f"commitChanges() returned {committed} but the server still holds "
            f"{marker!r}: {after}. {layer.commitErrors()}"
        )
        assert after == before, (
            f"the delete left {after} rather than restoring {before}; it removed "
            "or added the wrong row"
        )
        oapif_evidence.record(
            "NB-PQG-OAPIFT-03", "pass", measured_count=len(after),
            notes=(
                f"OGC API Features Part 4 delete removed {marker!r} and left "
                "every other row in place, confirmed server-side."
            ),
        )
