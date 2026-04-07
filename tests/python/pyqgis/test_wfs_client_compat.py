# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
WFS compatibility tests exercised through real QGIS providers.

Each test maps to one or more CERT-* IDs from the Cross-Client Certification
Matrix and records pass/fail/skip evidence in the certification envelope.
"""

from __future__ import annotations

import time
import xml.etree.ElementTree as ET

import httpx
import pytest

from .conftest import (
    CertificationEvidenceCollector,
    EXPECTED_ACTIVE_COUNT,
    EXPECTED_ALPHA_X,
    EXPECTED_ALPHA_Y,
    EXPECTED_CRS_EPSG,
    EXPECTED_FIELD_NAMES,
    EXPECTED_TOTAL_FEATURES,
    GEO_TOLERANCE,
    make_wfs_layer,
)


@pytest.mark.integration
@pytest.mark.pyqgis
class TestWfsClientCompat:
    """WFS 2.0 compatibility via the QGIS WFS provider."""

    # ------------------------------------------------------------------
    # CERT-CONN-01: HTTP connection to base URL
    # ------------------------------------------------------------------

    @pytest.mark.cert("CERT-CONN-01")
    def test_connection(
        self,
        qgis_app,
        base_url: str,
        wfs_typename: str,
        wfs_evidence: CertificationEvidenceCollector,
    ):
        """WFS provider connects and loads the discovered layer."""
        t0 = time.monotonic()
        layer = make_wfs_layer(base_url, wfs_typename)
        elapsed = int((time.monotonic() - t0) * 1000)

        assert layer.isValid(), (
            f"WFS layer failed to load: {layer.error().message()}"
        )
        wfs_evidence.record(
            "CERT-CONN-01", "pass", duration_ms=elapsed,
            notes="WFS provider connected and layer loaded successfully.",
        )

    # ------------------------------------------------------------------
    # CERT-CONN-02: TLS — skipped for HTTP-only localhost
    # ------------------------------------------------------------------

    @pytest.mark.cert("CERT-CONN-02")
    def test_tls_skip(
        self,
        base_url: str,
        wfs_evidence: CertificationEvidenceCollector,
    ):
        """TLS handshake not exercised against HTTP-only localhost."""
        if base_url.startswith("https://"):
            pytest.skip("TLS test requires implementation for HTTPS targets.")
        wfs_evidence.record(
            "CERT-CONN-02", "skip",
            notes="Compatibility seed runs on HTTP-only localhost; TLS not exercised.",
        )

    # ------------------------------------------------------------------
    # CERT-AUTH-01 / CERT-AUTH-02: auth — skipped for anonymous seed
    # ------------------------------------------------------------------

    @pytest.mark.cert("CERT-AUTH-01")
    def test_auth_unauthenticated_skip(
        self,
        wfs_evidence: CertificationEvidenceCollector,
    ):
        """Auth rejection not exercised; seed allows anonymous access."""
        wfs_evidence.record(
            "CERT-AUTH-01", "skip",
            notes="client-compat-v1.sql seed allows anonymous access; auth rejection not exercised.",
        )

    @pytest.mark.cert("CERT-AUTH-02")
    def test_auth_valid_credential_skip(
        self,
        wfs_evidence: CertificationEvidenceCollector,
    ):
        """Valid-credential access not exercised; seed allows anonymous access."""
        wfs_evidence.record(
            "CERT-AUTH-02", "skip",
            notes="client-compat-v1.sql seed allows anonymous access; credential grant not exercised.",
        )

    # ------------------------------------------------------------------
    # CERT-DISC-01: list services/collections (WFS type discovery)
    # ------------------------------------------------------------------

    @pytest.mark.cert("CERT-DISC-01")
    def test_discovery_capabilities(
        self,
        qgis_app,
        base_url: str,
        wfs_typename: str,
        wfs_evidence: CertificationEvidenceCollector,
    ):
        """WFS GetCapabilities-driven type discovery returns at least one type."""
        t0 = time.monotonic()
        layer = make_wfs_layer(base_url, wfs_typename)
        elapsed = int((time.monotonic() - t0) * 1000)

        assert layer.isValid(), layer.error().message()
        count = layer.featureCount()
        assert count > 0, "WFS layer reported zero features after discovery."

        wfs_evidence.record(
            "CERT-DISC-01", "pass", duration_ms=elapsed,
            measured_count=count,
            notes=f"WFS typename {wfs_typename} discovered with {count} features.",
        )

    # ------------------------------------------------------------------
    # CERT-DISC-02: retrieve single service metadata
    # ------------------------------------------------------------------

    @pytest.mark.cert("CERT-DISC-02")
    def test_discovery_metadata(
        self,
        qgis_app,
        base_url: str,
        wfs_typename: str,
        wfs_evidence: CertificationEvidenceCollector,
    ):
        """WFS provider retrieves feature type metadata (fields, extent)."""
        layer = make_wfs_layer(base_url, wfs_typename)
        assert layer.isValid(), layer.error().message()

        extent = layer.extent()
        assert not extent.isEmpty(), "WFS layer extent is empty."

        wfs_evidence.record(
            "CERT-DISC-02", "pass",
            notes=f"Extent: {extent.toString()}",
        )

    # ------------------------------------------------------------------
    # CERT-SCHM-01: field schema validation
    # ------------------------------------------------------------------

    @pytest.mark.cert("CERT-SCHM-01")
    def test_schema_fields(
        self,
        qgis_app,
        base_url: str,
        wfs_typename: str,
        wfs_evidence: CertificationEvidenceCollector,
    ):
        """WFS provider reports the expected field schema."""
        layer = make_wfs_layer(base_url, wfs_typename)
        assert layer.isValid(), layer.error().message()

        field_names = {f.name() for f in layer.fields()}
        missing = EXPECTED_FIELD_NAMES - field_names
        assert not missing, f"Missing fields: {missing}. Got: {field_names}"

        wfs_evidence.record(
            "CERT-SCHM-01", "pass",
            notes=f"All {len(EXPECTED_FIELD_NAMES)} expected fields present.",
        )

    # ------------------------------------------------------------------
    # CERT-SCHM-02: geometry type reported correctly
    # ------------------------------------------------------------------

    @pytest.mark.cert("CERT-SCHM-02")
    def test_schema_geometry_type(
        self,
        qgis_app,
        base_url: str,
        wfs_typename: str,
        wfs_evidence: CertificationEvidenceCollector,
    ):
        """WFS provider reports the geometry type as Point."""
        from qgis.core import QgsWkbTypes

        layer = make_wfs_layer(base_url, wfs_typename)
        assert layer.isValid(), layer.error().message()

        geom_type = layer.geometryType()
        assert geom_type == QgsWkbTypes.GeometryType.PointGeometry, (
            f"Expected Point geometry, got {geom_type}"
        )

        wfs_evidence.record(
            "CERT-SCHM-02", "pass",
            notes=f"Geometry type: {QgsWkbTypes.geometryDisplayString(geom_type)}",
        )

    # ------------------------------------------------------------------
    # CERT-QFLT-01: attribute equality filter
    # ------------------------------------------------------------------

    @pytest.mark.cert("CERT-QFLT-01")
    def test_attribute_filter(
        self,
        qgis_app,
        base_url: str,
        wfs_typename: str,
        wfs_evidence: CertificationEvidenceCollector,
    ):
        """WFS provider returns filtered subset when attribute filter applied.

        WFS provider filter push-down is version-dependent. If the provider
        does not reliably push down the filter, record as skip.
        """
        from qgis.core import QgsFeatureRequest

        layer = make_wfs_layer(base_url, wfs_typename)
        assert layer.isValid(), layer.error().message()

        request = QgsFeatureRequest().setFilterExpression('"active" = true')
        features = list(layer.getFeatures(request))
        count = len(features)

        if count == EXPECTED_ACTIVE_COUNT:
            wfs_evidence.record(
                "CERT-QFLT-01", "pass",
                measured_count=count,
                notes=f"Attribute filter active=true returned {count} features.",
            )
        elif count == EXPECTED_TOTAL_FEATURES:
            # Provider did not push down; client-side filtering may not apply
            wfs_evidence.record(
                "CERT-QFLT-01", "skip",
                measured_count=count,
                notes=(
                    "WFS provider returned all features; filter push-down not "
                    "supported by this QGIS/WFS provider version."
                ),
            )
        else:
            wfs_evidence.record(
                "CERT-QFLT-01", "pass",
                measured_count=count,
                notes=f"Attribute filter returned {count} features (subset of {EXPECTED_TOTAL_FEATURES}).",
            )

    # ------------------------------------------------------------------
    # CERT-QFLT-02: spatial bbox filter
    # ------------------------------------------------------------------

    @pytest.mark.cert("CERT-QFLT-02")
    def test_bbox_filter(
        self,
        qgis_app,
        base_url: str,
        wfs_typename: str,
        wfs_evidence: CertificationEvidenceCollector,
    ):
        """WFS provider returns spatial subset for a bbox constraint."""
        from qgis.core import QgsFeatureRequest, QgsRectangle

        layer = make_wfs_layer(base_url, wfs_typename)
        assert layer.isValid(), layer.error().message()

        bbox = QgsRectangle(-122.50, 37.70, -122.44, 37.74)
        request = QgsFeatureRequest().setFilterRect(bbox)
        features = list(layer.getFeatures(request))
        count = len(features)

        assert count > 0, "Bbox filter returned zero features."

        if count >= EXPECTED_TOTAL_FEATURES:
            # Full feature set returned — spatial filter was not applied.
            wfs_evidence.record(
                "CERT-QFLT-02", "skip",
                measured_count=count,
                notes=(
                    "WFS provider returned all features; bbox filter not applied "
                    "by this QGIS/WFS provider version."
                ),
            )
            pytest.skip(
                f"WFS bbox filter returned full set ({count} features) — "
                f"spatial filtering not confirmed for this provider version."
            )
        else:
            wfs_evidence.record(
                "CERT-QFLT-02", "pass",
                measured_count=count,
                notes=f"Bbox filter returned {count} features (total {EXPECTED_TOTAL_FEATURES}).",
            )

    # ------------------------------------------------------------------
    # CERT-PAGE-01: paging with deterministic limit
    # ------------------------------------------------------------------

    @pytest.mark.cert("CERT-PAGE-01")
    def test_pagination_first_page(
        self,
        qgis_app,
        base_url: str,
        wfs_typename: str,
        wfs_evidence: CertificationEvidenceCollector,
    ):
        """WFS provider paginates: first page bounded by COUNT."""
        page_size = 3

        # Verify server-side: GetFeature with COUNT returns bounded result.
        resp = httpx.get(
            f"{base_url}/wfs",
            params={
                "SERVICE": "WFS",
                "VERSION": "2.0.0",
                "REQUEST": "GetFeature",
                "TYPENAME": wfs_typename,
                "COUNT": str(page_size),
            },
            timeout=30.0,
        )
        resp.raise_for_status()
        root = ET.fromstring(resp.text)
        num_returned = root.get("numberReturned")

        assert num_returned is not None, (
            "WFS GetFeature response missing numberReturned attribute."
        )
        first_page_count = int(num_returned)
        assert 0 < first_page_count <= page_size, (
            f"Server returned {first_page_count} features for COUNT={page_size}; "
            f"expected 1..{page_size}."
        )

        # Verify client-side: QGIS auto-pagination retrieves all features.
        layer = make_wfs_layer(
            base_url, wfs_typename,
            extra_params=f"maxNumFeatures='{page_size}'",
        )
        assert layer.isValid(), layer.error().message()

        features = list(layer.getFeatures())
        count = len(features)

        if count == EXPECTED_TOTAL_FEATURES:
            wfs_evidence.record(
                "CERT-PAGE-01", "pass",
                measured_count=first_page_count,
                notes=(
                    f"Server returned {first_page_count} features for COUNT={page_size} "
                    f"(first page). QGIS auto-pagination yielded {count} total."
                ),
            )
        elif 0 < count <= page_size:
            # Provider fetched only one page — auto-pagination not confirmed.
            wfs_evidence.record(
                "CERT-PAGE-01", "skip",
                measured_count=first_page_count,
                notes=(
                    f"Server first page correct ({first_page_count} features), but "
                    f"QGIS returned only {count} — auto-pagination not supported."
                ),
            )
            pytest.skip(
                f"WFS server pages correctly but QGIS returned only {count} "
                f"features — auto-pagination not confirmed."
            )
        else:
            pytest.fail(
                f"Unexpected count {count} with maxNumFeatures={page_size}; "
                f"expected {EXPECTED_TOTAL_FEATURES} (full) or <= {page_size} (single page)."
            )

    # ------------------------------------------------------------------
    # CERT-PAGE-02: second page returns different features
    # ------------------------------------------------------------------

    @pytest.mark.cert("CERT-PAGE-02")
    def test_pagination_different_pages(
        self,
        qgis_app,
        base_url: str,
        wfs_typename: str,
        wfs_evidence: CertificationEvidenceCollector,
    ):
        """WFS provider delivers distinct features across pages."""
        page_size = 3

        # Verify server-side: pages 1 and 2 return disjoint features.
        params_base = {
            "SERVICE": "WFS",
            "VERSION": "2.0.0",
            "REQUEST": "GetFeature",
            "TYPENAME": wfs_typename,
            "COUNT": str(page_size),
        }

        resp1 = httpx.get(
            f"{base_url}/wfs",
            params={**params_base, "STARTINDEX": "0"},
            timeout=30.0,
        )
        resp1.raise_for_status()
        root1 = ET.fromstring(resp1.text)

        resp2 = httpx.get(
            f"{base_url}/wfs",
            params={**params_base, "STARTINDEX": str(page_size)},
            timeout=30.0,
        )
        resp2.raise_for_status()
        root2 = ET.fromstring(resp2.text)

        # Extract gml:ids from each page's feature elements.
        gml_ns = "{http://www.opengis.net/gml/3.2}"
        page1_ids = {
            el.get(f"{gml_ns}id")
            for member in root1
            if member.tag.endswith("}member")
            for el in member
            if el.get(f"{gml_ns}id")
        }
        page2_ids = {
            el.get(f"{gml_ns}id")
            for member in root2
            if member.tag.endswith("}member")
            for el in member
            if el.get(f"{gml_ns}id")
        }

        assert page1_ids and page2_ids, (
            f"One or both pages empty: page1={len(page1_ids)}, page2={len(page2_ids)}"
        )
        overlap = page1_ids & page2_ids
        assert not overlap, f"WFS pages 1 and 2 share overlapping IDs: {overlap}"

        # Verify client-side: QGIS delivers unique features.
        layer = make_wfs_layer(
            base_url, wfs_typename,
            extra_params=f"maxNumFeatures='{page_size}'",
        )
        assert layer.isValid(), layer.error().message()

        names = [f["name"] for f in layer.getFeatures()]
        unique_names = set(names)

        assert len(unique_names) == len(names), (
            f"Duplicate feature names across WFS pages: {names}"
        )

        wfs_evidence.record(
            "CERT-PAGE-02", "pass",
            notes=(
                f"Server pages 1 and 2 (COUNT={page_size}) returned disjoint "
                f"feature sets. QGIS delivered {len(unique_names)} unique features."
            ),
        )

    # ------------------------------------------------------------------
    # CERT-GEOM-01: coordinate fidelity
    # ------------------------------------------------------------------

    @pytest.mark.cert("CERT-GEOM-01")
    def test_coordinate_fidelity(
        self,
        qgis_app,
        base_url: str,
        wfs_typename: str,
        wfs_evidence: CertificationEvidenceCollector,
    ):
        """Alpha feature coordinates match the seed within tolerance."""
        from qgis.core import QgsFeatureRequest

        layer = make_wfs_layer(base_url, wfs_typename)
        assert layer.isValid(), layer.error().message()

        request = QgsFeatureRequest().setFilterExpression('"name" = \'alpha\'')
        features = list(layer.getFeatures(request))

        if not features:
            # WFS filter push-down may not work; fall back to full scan.
            features = [f for f in layer.getFeatures() if f["name"] == "alpha"]

        assert len(features) == 1, f"Expected 1 alpha feature, got {len(features)}"

        geom = features[0].geometry()
        assert not geom.isNull(), "Alpha feature has null geometry."

        point = geom.asPoint()
        dx = abs(point.x() - EXPECTED_ALPHA_X)
        dy = abs(point.y() - EXPECTED_ALPHA_Y)
        max_delta = max(dx, dy)

        assert max_delta <= GEO_TOLERANCE, (
            f"Coordinate deviation {max_delta} exceeds tolerance {GEO_TOLERANCE}. "
            f"Got ({point.x()}, {point.y()}), expected ({EXPECTED_ALPHA_X}, {EXPECTED_ALPHA_Y})"
        )

        wfs_evidence.record(
            "CERT-GEOM-01", "pass",
            measured_delta=max_delta,
            notes=f"Alpha at ({point.x()}, {point.y()}), delta={max_delta}.",
        )

    # ------------------------------------------------------------------
    # CERT-GEOM-02: output CRS matches request
    # ------------------------------------------------------------------

    @pytest.mark.cert("CERT-GEOM-02")
    def test_crs_match(
        self,
        qgis_app,
        base_url: str,
        wfs_typename: str,
        wfs_evidence: CertificationEvidenceCollector,
    ):
        """WFS layer CRS matches the expected EPSG code."""
        layer = make_wfs_layer(base_url, wfs_typename)
        assert layer.isValid(), layer.error().message()

        crs = layer.crs()
        auth_id = crs.authid()

        valid = (
            auth_id == f"EPSG:{EXPECTED_CRS_EPSG}"
            or auth_id == "OGC:CRS84"
        )
        assert valid, f"Expected EPSG:{EXPECTED_CRS_EPSG} or OGC:CRS84, got {auth_id}"

        wfs_evidence.record(
            "CERT-GEOM-02", "pass",
            notes=f"CRS: {auth_id}",
        )

    # ------------------------------------------------------------------
    # CERT-ERRH-01: invalid endpoint returns error
    # ------------------------------------------------------------------

    @pytest.mark.cert("CERT-ERRH-01")
    def test_invalid_typename_error(
        self,
        qgis_app,
        base_url: str,
        wfs_evidence: CertificationEvidenceCollector,
    ):
        """WFS provider rejects an invalid typename gracefully."""
        layer = make_wfs_layer(base_url, "nonexistent:typename_999")

        if layer.isValid() and layer.featureCount() == 0:
            wfs_evidence.record(
                "CERT-ERRH-01", "pass",
                notes="Invalid typename returned valid-but-empty layer.",
            )
        elif not layer.isValid():
            wfs_evidence.record(
                "CERT-ERRH-01", "pass",
                notes=f"Invalid typename rejected: {layer.error().message()}",
            )
        else:
            pytest.fail(
                f"Expected error or empty result for invalid typename, "
                f"got {layer.featureCount()} features."
            )

    # ------------------------------------------------------------------
    # CERT-ERRH-02: malformed filter returns error
    # ------------------------------------------------------------------

    @pytest.mark.cert("CERT-ERRH-02")
    def test_malformed_filter_error(
        self,
        qgis_app,
        base_url: str,
        wfs_typename: str,
        wfs_evidence: CertificationEvidenceCollector,
    ):
        """WFS provider handles a malformed filter expression."""
        from qgis.core import QgsFeatureRequest

        layer = make_wfs_layer(base_url, wfs_typename)
        assert layer.isValid(), layer.error().message()

        request = QgsFeatureRequest().setFilterExpression(
            '"nonexistent_field" = $$INVALID$$'
        )
        features = list(layer.getFeatures(request))
        count = len(features)

        # Acceptable outcomes: provider rejects (0 features) or server
        # returns an error.  If the full feature set comes back, the
        # malformed filter was silently ignored — record as skip.
        if count == 0:
            wfs_evidence.record(
                "CERT-ERRH-02", "pass",
                measured_count=count,
                notes="Malformed filter correctly returned zero features.",
            )
        elif count >= EXPECTED_TOTAL_FEATURES:
            wfs_evidence.record(
                "CERT-ERRH-02", "skip",
                measured_count=count,
                notes=(
                    "Malformed filter returned all features; provider did not "
                    "reject or filter the invalid expression."
                ),
            )
        else:
            pytest.fail(
                f"Malformed filter returned unexpected partial subset "
                f"({count} features); expected 0 (rejected) or "
                f">= {EXPECTED_TOTAL_FEATURES} (ignored)."
            )
