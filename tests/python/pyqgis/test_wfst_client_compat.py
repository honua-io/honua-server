# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""WFS-T 2.0 transactions exercised through the real QGIS WFS provider.

The QGIS manual documents WFS-T create/modify/delete, and the server advertises
it: GetCapabilities carries ``ows:Operation name="Transaction"`` and
``ImplementsTransactionalWFS`` with ``DefaultValue TRUE``. The provider confirms
it per layer - ``capabilities()`` on each scratch type reports AddFeatures,
DeleteFeatures, ChangeAttributeValues and ChangeGeometries.

Three facts about this surface are easy to get wrong and cost real time here:

**Writes require authentication; reads do not.** An anonymous Transaction returns
``NoApplicableCode`` / "Authentication is required to access this resource", which
reaches QGIS only as ``ERROR: 1 feature(s) not added.`` The accepted scheme is the
``X-API-Key`` header - HTTP Basic is refused with the same message. The provider
sends a header on a Transaction only from an entry in QGIS's authentication
database referenced as ``authcfg=<id>``; ``http-header:`` and username/password
in the URI never reach the write path. The lane provisions that entry itself
(``conftest.api_header_authcfg``) from the credential named by the environment
at call time, in the session's own in-process auth database, so the key is never
written into an envelope or onto disk.

**The scratch types are shared with the OWSLib lane.** Collections 10, 11 and 12
are "Per-test OWSLib Insert/Update/Delete certification layer", and that lane
leaves rows behind. So nothing here asserts an absolute feature count: each case
records the count it found, asserts on its own marker, and restores what it
found. A count assertion would pass or fail depending on whether OWSLib ran
first.

**The OGC API Features collection id is numeric, not the type name.** The
verification read uses 10/11/12; ``/collections/wfs_t_insert_scratch`` is a 404.

Verification is always an independent read rather than the client's own report:
``commitChanges()`` has been observed returning True while the server rejected
the write.
"""

from __future__ import annotations

import json
import urllib.parse
import urllib.request

import pytest

from .conftest import (
    CertificationEvidenceCollector,
    make_wfs_layer,
)

INSERT_TYPE = "honua:wfs_t_insert_scratch"
UPDATE_TYPE = "honua:wfs_t_update_scratch"
DELETE_TYPE = "honua:wfs_t_delete_scratch"

#: WFS type name -> OGC API Features collection id used for the independent read.
COLLECTION_IDS = {
    INSERT_TYPE: "10",
    UPDATE_TYPE: "11",
    DELETE_TYPE: "12",
}

#: Inside the browser_compat cluster, so an inserted point lands in the fixture
#: envelope rather than at (0, 0).
TEST_LON = -122.4188
TEST_LAT = 37.7742


def _read_names(base_url: str, typename: str) -> list[str]:
    """Names currently held by a scratch type, read independently of QGIS."""
    url = (f"{base_url.rstrip('/')}/ogc/features/collections/"
           f"{COLLECTION_IDS[typename]}/items?" + urllib.parse.urlencode({
               "limit": 200, "f": "json"}))
    with urllib.request.urlopen(url, timeout=30) as response:
        features = json.load(response).get("features", [])
    return sorted(
        str((feature.get("properties") or {}).get("name")) for feature in features)


@pytest.mark.integration
@pytest.mark.pyqgis
class TestWfsTransactionClientCompat:
    """WFS-T through the QGIS WFS provider, verified server-side."""

    def _editable(self, base_url: str, typename: str, authcfg: str | None):
        from qgis.core import QgsVectorDataProvider

        if authcfg is None:
            pytest.skip(
                "HONUA_ADMIN_PASSWORD is unset, and a WFS Transaction is refused "
                "anonymously with 'Authentication is required to access this "
                "resource'; the write path cannot be exercised without it")

        # The session's own API-header authcfg (conftest.api_header_authcfg):
        # the one credential transport the WFS provider honours on a
        # Transaction. Inline URI credentials - http-header:, username/password -
        # never reached the write path; a no-auth control failed identically and
        # the provider's decodeUri parsed none of them.
        layer = make_wfs_layer(
            base_url, typename,
            extra_params=f"authcfg='{authcfg}'")
        assert layer.isValid(), (
            f"{typename} did not load, so no transaction can be attempted: "
            f"{layer.error().summary()}"
        )

        # The read path is anonymous, so the layer loads either way; the write
        # path is not. Probe it before the case runs so a rejected trial edit
        # fails here, with the credential transport named, instead of inside a
        # case that would then misattribute it to the Transaction itself.
        assert self._write_path_is_authenticated(layer), (
            "a trial edit did not commit even though the layer carries the "
            f"session authcfg {authcfg!r}; the WFS provider did not deliver the "
            "X-API-Key header on the Transaction, or the server refused it")
        capabilities = layer.dataProvider().capabilities()
        for required in ("AddFeatures", "DeleteFeatures", "ChangeAttributeValues"):
            bit = getattr(QgsVectorDataProvider, required)
            assert capabilities & bit, (
                f"{typename} does not advertise {required}, so the server's "
                "ImplementsTransactionalWFS=TRUE is not reaching the client"
            )
        return layer


    @staticmethod
    def _write_path_is_authenticated(layer) -> bool:
        """True when a trial edit commits, i.e. the provider can authenticate.

        Probed rather than assumed: the layer is valid either way because reads
        are anonymous, so validity says nothing about the write path.
        """
        from qgis.core import QgsFeature, QgsGeometry, QgsPointXY

        probe = QgsFeature(layer.fields())
        probe.setAttribute("name", "pyqgis-wfst-authprobe")
        probe.setGeometry(QgsGeometry.fromPointXY(QgsPointXY(TEST_LON, TEST_LAT)))
        if not layer.startEditing():
            return False
        layer.addFeature(probe)
        if not layer.commitChanges():
            layer.rollBack()
            return False
        # Authenticated after all: remove the probe row before the case runs.
        layer.reload()
        target = next(
            (f for f in layer.getFeatures()
             if f.attribute("name") == "pyqgis-wfst-authprobe"), None)
        if target is not None:
            layer.startEditing()
            layer.deleteFeature(target.id())
            layer.commitChanges()
        return True

    def _add(self, layer, marker: str) -> bool:
        from qgis.core import QgsFeature, QgsGeometry, QgsPointXY

        feature = QgsFeature(layer.fields())
        feature.setAttribute("name", marker)
        feature.setGeometry(QgsGeometry.fromPointXY(QgsPointXY(TEST_LON, TEST_LAT)))
        assert layer.startEditing(), "the provider refused to begin an edit session"
        assert layer.addFeature(feature), "addFeature was rejected client-side"
        return layer.commitChanges()

    def _delete_marker(self, layer, base_url: str, typename: str, marker: str) -> bool:
        layer.reload()
        target = next(
            (f for f in layer.getFeatures() if f.attribute("name") == marker), None)
        if target is None:
            return marker not in _read_names(base_url, typename)
        assert layer.startEditing()
        assert layer.deleteFeature(target.id()), "deleteFeature was rejected client-side"
        return layer.commitChanges()

    # NB-PQG-WFST-01 / Transaction-Insert.
    def test_insert_creates_a_feature_on_the_server(
        self, qgis_app, base_url: str,
        wfs_evidence: CertificationEvidenceCollector,
        api_header_authcfg: str | None,
    ) -> None:
        layer = self._editable(base_url, INSERT_TYPE, api_header_authcfg)
        before = _read_names(base_url, INSERT_TYPE)
        marker = "pyqgis-wfst-insert"
        assert marker not in before, (
            f"{marker!r} is already present, so an earlier run did not clean up")

        committed = self._add(layer, marker)
        try:
            after = _read_names(base_url, INSERT_TYPE)
            assert marker in after, (
                f"commitChanges() returned {committed} but the server does not "
                f"hold {marker!r}; it holds {after}. {layer.commitErrors()}"
            )
            assert len(after) == len(before) + 1, (
                f"the insert changed the row count by {len(after) - len(before)}, "
                "not 1")
            wfs_evidence.record(
                "NB-PQG-WFST-01", "pass", measured_count=len(after),
                notes=(
                    f"WFS-T Insert through the QGIS provider created {marker!r}, "
                    "confirmed by an independent OGC API Features read rather "
                    "than by commitChanges(). Authenticated with the X-API-Key "
                    "header; an anonymous Transaction is refused."
                ),
            )
        finally:
            restored = self._delete_marker(layer, base_url, INSERT_TYPE, marker)
            assert marker not in _read_names(base_url, INSERT_TYPE), (
                f"cleanup failed (commit returned {restored}); {INSERT_TYPE} "
                f"still holds {marker!r}, which would drift the next run"
            )

    # NB-PQG-WFST-02 / Transaction-Update.
    def test_update_changes_an_attribute_on_the_server(
        self, qgis_app, base_url: str,
        wfs_evidence: CertificationEvidenceCollector,
        api_header_authcfg: str | None,
    ) -> None:
        layer = self._editable(base_url, UPDATE_TYPE, api_header_authcfg)
        before = _read_names(base_url, UPDATE_TYPE)
        marker = "pyqgis-wfst-update"
        assert marker not in before, (
            f"{marker!r} is already present, so an earlier run did not clean up")

        # Update a row this case owns, so a shared scratch row is never rewritten.
        seed_marker = "pyqgis-wfst-update-seed"
        assert self._add(layer, seed_marker), (
            f"seeding the update target failed: {layer.commitErrors()}")
        layer.reload()
        target = next(
            (f for f in layer.getFeatures() if f.attribute("name") == seed_marker),
            None)
        assert target is not None, "the seeded target is not visible to the provider"

        field = layer.fields().indexOf("name")
        assert field >= 0, "the scratch type has no 'name' field to change"
        assert layer.startEditing()
        assert layer.changeAttributeValue(target.id(), field, marker), (
            "changeAttributeValue was rejected client-side")
        committed = layer.commitChanges()

        try:
            after = _read_names(base_url, UPDATE_TYPE)
            assert marker in after, (
                f"commitChanges() returned {committed} but the server still "
                f"holds {after}. {layer.commitErrors()}"
            )
            assert seed_marker not in after, (
                "the update inserted a new row instead of modifying the target")
            assert len(after) == len(before) + 1, (
                "the update changed the row count beyond the one row this case added")
            wfs_evidence.record(
                "NB-PQG-WFST-02", "pass",
                notes=(
                    f"WFS-T Update rewrote 'name' from {seed_marker!r} to "
                    f"{marker!r} in place, confirmed server-side, with no extra "
                    "row created."
                ),
            )
        finally:
            for leftover in (marker, seed_marker):
                self._delete_marker(layer, base_url, UPDATE_TYPE, leftover)
            remaining = _read_names(base_url, UPDATE_TYPE)
            assert marker not in remaining and seed_marker not in remaining, (
                f"cleanup failed; {UPDATE_TYPE} still holds {remaining}")

    # NB-PQG-WFST-03 / Transaction-Delete.
    def test_delete_removes_a_feature_from_the_server(
        self, qgis_app, base_url: str,
        wfs_evidence: CertificationEvidenceCollector,
        api_header_authcfg: str | None,
    ) -> None:
        layer = self._editable(base_url, DELETE_TYPE, api_header_authcfg)
        before = _read_names(base_url, DELETE_TYPE)
        marker = "pyqgis-wfst-delete"
        assert marker not in before, (
            f"{marker!r} is already present, so an earlier run did not clean up")

        # Delete a row this case created, so the delete has an unambiguous target
        # and a shared scratch row is never destroyed.
        assert self._add(layer, marker), (
            f"seeding the delete target failed: {layer.commitErrors()}")
        assert marker in _read_names(base_url, DELETE_TYPE), (
            "the delete target was not created, so the delete would prove nothing")

        committed = self._delete_marker(layer, base_url, DELETE_TYPE, marker)

        after = _read_names(base_url, DELETE_TYPE)
        assert marker not in after, (
            f"commitChanges() returned {committed} but the server still holds "
            f"{marker!r}: {after}. {layer.commitErrors()}"
        )
        assert after == before, (
            f"the delete left {after} rather than restoring {before}; it removed "
            "or added the wrong row"
        )
        wfs_evidence.record(
            "NB-PQG-WFST-03", "pass", measured_count=len(after),
            notes=(
                f"WFS-T Delete removed {marker!r} and left every other row "
                "intact, confirmed server-side."
            ),
        )
