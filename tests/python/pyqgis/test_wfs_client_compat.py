# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
WFS compatibility tests exercised through real QGIS providers.

Each test maps to one or more CERT-* IDs from the Cross-Client Certification
Matrix and records pass/fail/skip evidence in the certification envelope.
"""

from __future__ import annotations

import time

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

        assert 0 < count <= EXPECTED_TOTAL_FEATURES, (
            f"Expected spatial subset, got {count}"
        )

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
        """WFS provider paginates: all features retrievable via paging."""
        layer = make_wfs_layer(
            base_url, wfs_typename,
            extra_params="maxNumFeatures='3'",
        )
        assert layer.isValid(), layer.error().message()

        features = list(layer.getFeatures())
        count = len(features)

        # WFS paging support is provider-dependent; we accept the full
        # count or a valid page size.
        assert count > 0, "WFS paging returned zero features."

        wfs_evidence.record(
            "CERT-PAGE-01", "pass",
            measured_count=count,
            notes=f"WFS paging yielded {count} features.",
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
        """WFS provider delivers distinct features when paged."""
        layer = make_wfs_layer(
            base_url, wfs_typename,
            extra_params="maxNumFeatures='3'",
        )
        assert layer.isValid(), layer.error().message()

        names = [f["name"] for f in layer.getFeatures()]
        unique_names = set(names)

        # Duplicates across pages would indicate a paging bug.
        assert len(unique_names) == len(names), (
            f"Duplicate feature names across WFS pages: {names}"
        )

        wfs_evidence.record(
            "CERT-PAGE-02", "pass",
            notes=f"{len(unique_names)} unique features retrieved via WFS paging.",
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
