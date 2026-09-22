# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""OGC API - Styles compatibility exercised through the real QGIS SLD reader.

This cell was recorded in the certification checklist as ``blocked`` with the
cause "landing reports an empty styles array; none published". That was false:
``/ogc/styles`` serves 8 styles with negotiable SLD 1.0/1.1 and Mapbox
representations, and QGIS applies the SLD verbatim. The cell is reachable, which
is why it is certified here rather than closed.

QGIS has no OGC API - Styles client: there is no ``styles`` entry in
``QgsProviderRegistry`` and no style-service browser. What QGIS does have is an
SLD reader, ``QgsVectorLayer.loadSldStyle``, which is the path a user takes after
downloading a style document. So the certified claim is precisely that: the
server's advertised SLD representation is one this client can fetch and apply,
and the symbology that results is the server's rather than QGIS's default.

That last part is what makes the case non-vacuous. ``loadSldStyle`` returns
``("", True)`` for a document it merely tolerates, so success is asserted against
the rendered colour instead - QGIS assigns every new layer a *random* fill, so a
colour matching the server's published symbology cannot be a default.
"""

from __future__ import annotations

import time
import xml.etree.ElementTree as ET

import httpx
import pytest

from .conftest import (
    CertificationEvidenceCollector,
    make_oapif_layer,
)

STYLES_PATH = "/ogc/styles"

# The style published for test_service layer 0, and the symbology it carries.
STYLE_ID = "Test Layer"
COLLECTION_ID = "0"
SLD_10_MEDIA_TYPE = "application/vnd.ogc.sld+xml;version=1.0"

# #2D69A5 at fill-opacity 0.85 -> 216/255 alpha.
EXPECTED_FILL = (45, 105, 165)
EXPECTED_ALPHA = 216


@pytest.mark.integration
@pytest.mark.pyqgis
class TestOgcApiStylesClientCompat:
    """OGC API - Styles, consumed the only way QGIS can consume it."""

    # CERT-DISC-01 / style discovery.
    def test_landing_advertises_published_styles(
        self, qgis_app, base_url: str,
        ogcstyles_evidence: CertificationEvidenceCollector,
    ) -> None:
        started = time.monotonic()
        response = httpx.get(f"{base_url}{STYLES_PATH}", timeout=30)
        response.raise_for_status()
        body = response.json()

        styles = body.get("styles", [])
        assert styles, (
            "the styles landing page advertises nothing, so no client can "
            f"discover a style: {body!r}"
        )
        identifiers = {style.get("id") for style in styles}
        assert STYLE_ID in identifiers, (
            f"style {STYLE_ID!r} is not advertised; got {sorted(identifiers)}")
        ogcstyles_evidence.record(
            "CERT-DISC-01", "pass", measured_count=len(styles),
            duration_ms=int((time.monotonic() - started) * 1000),
            notes=(
                f"{len(styles)} styles advertised at {STYLES_PATH}; "
                f"{STYLE_ID!r} among them."
            ),
        )

    # CERT-RNDR-SYM-01 / the style is one this client can actually apply.
    def test_advertised_sld_is_applied_by_qgis(
        self, qgis_app, base_url: str, tmp_path,
        ogcstyles_evidence: CertificationEvidenceCollector,
    ) -> None:
        response = httpx.get(
            f"{base_url}{STYLES_PATH}/{httpx.URL(STYLE_ID).path}",
            headers={"Accept": SLD_10_MEDIA_TYPE},
            timeout=30,
        )
        response.raise_for_status()
        assert response.text.strip().startswith("<"), (
            f"the SLD representation is not XML: {response.text[:120]!r}")
        # Parse before handing it to QGIS: loadSldStyle reports success for a
        # document it cannot really use, so a malformed body must fail here.
        ET.fromstring(response.text)

        sld_path = tmp_path / "style.sld"
        sld_path.write_text(response.text, encoding="utf-8", newline="\n")

        layer = make_oapif_layer(base_url, COLLECTION_ID)
        assert layer.isValid(), (
            f"the OAPIF layer did not load, so there is nothing to style: "
            f"{layer.error().summary()}"
        )
        default_colour = layer.renderer().symbol().color().getRgb()

        message, ok = layer.loadSldStyle(str(sld_path))

        assert ok, f"QGIS rejected the advertised SLD: {message}"
        applied = layer.renderer().symbol().color()
        assert (applied.red(), applied.green(), applied.blue()) == EXPECTED_FILL, (
            f"QGIS reported success but the symbology is not the server's: "
            f"expected RGB {EXPECTED_FILL}, got "
            f"{(applied.red(), applied.green(), applied.blue())}. QGIS assigns a "
            f"random default fill (this layer started at {default_colour}), so a "
            "non-matching colour means the SLD was tolerated, not applied."
        )
        assert applied.alpha() == EXPECTED_ALPHA, (
            f"the SLD fill-opacity did not survive: expected alpha "
            f"{EXPECTED_ALPHA}, got {applied.alpha()}"
        )
        ogcstyles_evidence.record(
            "CERT-RNDR-SYM-01", "pass",
            notes=(
                f"SLD 1.0 fetched by content negotiation and applied via "
                f"loadSldStyle; renderer fill became "
                f"{(applied.red(), applied.green(), applied.blue())} alpha "
                f"{applied.alpha()}, the server's published symbology rather than "
                f"QGIS's random default {default_colour}."
            ),
            evidence_ref=f"{STYLES_PATH}/{STYLE_ID}",
        )

    # CERT-ERRH-01. Paired with the advertised style, so it cannot pass merely
    # because the styles surface is unreachable.
    def test_unknown_style_is_not_served(
        self, qgis_app, base_url: str,
        ogcstyles_evidence: CertificationEvidenceCollector,
    ) -> None:
        advertised = httpx.get(
            f"{base_url}{STYLES_PATH}/{httpx.URL(STYLE_ID).path}",
            headers={"Accept": SLD_10_MEDIA_TYPE},
            timeout=30,
        )
        assert advertised.status_code == 200, (
            "the known-good style does not resolve, so this negative case proves "
            f"nothing: got {advertised.status_code}"
        )

        missing = httpx.get(
            f"{base_url}{STYLES_PATH}/NoSuchStyleWhatsoever",
            headers={"Accept": SLD_10_MEDIA_TYPE},
            timeout=30,
        )

        assert missing.status_code == 404, (
            "an unpublished style id was served, so a client cannot tell a real "
            f"style from a typo: got {missing.status_code}"
        )
        ogcstyles_evidence.record(
            "CERT-ERRH-01", "pass",
            notes="An unpublished style id returned 404 while the advertised one served 200.",
        )
