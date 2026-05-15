# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Enhanced OGC standards compliance tests for WFS 2.0 filter capabilities.
"""

import pytest
import httpx
from typing import Dict
from xml.etree import ElementTree as ET

from shared.temporal_operator_compliance import ALL_TEMPORAL_COMPLIANCE_CASES
from shared.spatial_function_compliance import ALL_SPATIAL_FUNCTION_CASES


SCHEMA_VALID_FES_TEMPORAL_OPERATORS = {
    "After",
    "Before",
    "Begins",
    "BegunBy",
    "TContains",
    "During",
    "TEquals",
    "TOverlaps",
    "Meets",
    "MetBy",
    "OverlappedBy",
    "EndedBy",
    "Ends",
    "AnyInteracts",
}


def _wfs_get_capabilities(http_client: httpx.Client) -> httpx.Response:
    return http_client.get(
        "/wfs",
        params={
            "SERVICE": "WFS",
            "VERSION": "2.0.0",
            "REQUEST": "GetCapabilities",
        },
    )


def _constraint_defaults(root: ET.Element) -> Dict[str, str]:
    ns = {"fes": "http://www.opengis.net/fes/2.0"}
    defaults: Dict[str, str] = {}
    for constraint in root.findall(".//fes:Constraint", ns):
        name = constraint.attrib.get("name")
        default_value = next(
            (
                element.text
                for element in constraint.iter()
                if element.tag.endswith("DefaultValue")
            ),
            None,
        )
        if name and default_value:
            defaults[name] = default_value
    return defaults


class TestEnhancedStandardsCompliance:
    """Tests for enhanced OGC standards compliance in WFS 2.0 implementation."""

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_wfs_getcapabilities_enhanced_filter_capabilities(
        self, http_client: httpx.Client
    ):
        """Test that WFS GetCapabilities returns enhanced FilterCapabilities."""
        response = _wfs_get_capabilities(http_client)
        assert response.status_code == 200

        # Parse XML response
        root = ET.fromstring(response.content)

        # Find FilterCapabilities section
        ns = {"wfs": "http://www.opengis.net/wfs/2.0", "fes": "http://www.opengis.net/fes/2.0"}
        filter_caps = root.find(".//fes:Filter_Capabilities", ns)
        assert filter_caps is not None, "FilterCapabilities should be present"

        # Verify enhanced conformance declarations
        conformance = filter_caps.find(".//fes:Conformance", ns)
        assert conformance is not None, "Conformance section should be present"

        constraints = conformance.findall(".//fes:Constraint", ns)
        constraint_names = {c.attrib["name"] for c in constraints}

        # Core OGC conformance classes
        required_constraints = {
            "ImplementsQuery",
            "ImplementsAdHocQuery",
            "ImplementsStandardFilter",
            "ImplementsSpatialFilter",
            "ImplementsTemporalFilter",
            "ImplementsFunctions"
        }

        for constraint in required_constraints:
            assert constraint in constraint_names, f"Missing required constraint: {constraint}"

        # Enhanced conformance classes
        enhanced_constraints = {
            "ImplementsCQL2Text",
            "ImplementsCQL2JSON",
            "ImplementsCQL2SpatialOperators",
            "ImplementsCQL2TemporalOperators"
        }

        for constraint in enhanced_constraints:
            assert constraint in constraint_names, f"Missing enhanced constraint: {constraint}"

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_wfs_temporal_operators_not_advertised_until_cite_stable(
        self, http_client: httpx.Client
    ):
        """Test that FES temporal operators stay unadvertised until CITE stability is proven."""
        response = _wfs_get_capabilities(http_client)
        assert response.status_code == 200

        root = ET.fromstring(response.content)
        ns = {"fes": "http://www.opengis.net/fes/2.0"}

        temporal_ops = root.findall(".//fes:TemporalOperator", ns)
        constraint_defaults = _constraint_defaults(root)

        assert temporal_ops == []
        assert constraint_defaults["ImplementsMinTemporalFilter"] == "FALSE"
        assert constraint_defaults["ImplementsTemporalFilter"] == "FALSE"
        assert constraint_defaults["ImplementsTemporalInstant"] == "FALSE"
        assert constraint_defaults["ImplementsTemporalPeriod"] == "FALSE"

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_wfs_unsupported_functions_not_advertised(
        self, http_client: httpx.Client
    ):
        """Test that unsupported FES functions are not advertised in capabilities."""
        response = _wfs_get_capabilities(http_client)
        assert response.status_code == 200

        root = ET.fromstring(response.content)
        ns = {"fes": "http://www.opengis.net/fes/2.0"}

        functions = root.findall(".//fes:Function", ns)
        advertised_functions = {func.attrib.get("name") for func in functions}
        constraint_defaults = _constraint_defaults(root)

        assert not advertised_functions, f"Unexpected advertised functions: {advertised_functions}"
        assert constraint_defaults["ImplementsFunctions"] == "FALSE"

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_wfs_function_conformance_matches_runtime_support(
        self, http_client: httpx.Client
    ):
        """Test FES function conformance flags match the WFS runtime surface."""
        response = _wfs_get_capabilities(http_client)
        assert response.status_code == 200

        root = ET.fromstring(response.content)
        ns = {"fes": "http://www.opengis.net/fes/2.0"}

        functions = root.findall(".//fes:Function", ns)
        constraint_defaults = _constraint_defaults(root)

        assert functions == []
        assert constraint_defaults["ImplementsFunctions"] == "FALSE"
        assert constraint_defaults["ImplementsArithmeticOperators"] == "FALSE"
        assert constraint_defaults["ImplementsCQL2Functions"] == "FALSE"

    @pytest.mark.integration
    @pytest.mark.ogc
    @pytest.mark.parametrize("name,expression,description", ALL_TEMPORAL_COMPLIANCE_CASES[:5])  # Test subset
    def test_enhanced_temporal_operators_parsing(
        self,
        http_client: httpx.Client,
        test_collection_id: str,
        name: str,
        expression: str,
        description: str,
    ):
        """Test that enhanced temporal operators parse correctly."""
        # Test CQL2-Text parsing by sending filter request
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"filter": expression, "limit": 1},
        )

        # Should parse successfully (200) or return meaningful error
        assert response.status_code in [200, 400], f"Unexpected status for {name}: {response.status_code}"

        if response.status_code == 400:
            # If error, should be meaningful filter error, not parsing error
            error_text = response.text.lower()
            assert "syntax" not in error_text, f"Syntax error in temporal expression {name}: {expression}"
            assert "parse" not in error_text, f"Parse error in temporal expression {name}: {expression}"

    @pytest.mark.integration
    @pytest.mark.ogc
    @pytest.mark.parametrize("name,expression,description", ALL_SPATIAL_FUNCTION_CASES[:5])  # Test subset
    def test_enhanced_spatial_functions_parsing(
        self,
        http_client: httpx.Client,
        test_collection_id: str,
        name: str,
        expression: str,
        description: str,
    ):
        """Test that enhanced spatial functions parse correctly."""
        # Test CQL2-Text parsing by sending filter request
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"filter": expression, "limit": 1},
        )

        # Should parse successfully (200) or return meaningful error
        assert response.status_code in [200, 400], f"Unexpected status for {name}: {response.status_code}"

        if response.status_code == 400:
            # If error, should be meaningful filter error, not parsing error
            error_text = response.text.lower()
            assert "syntax" not in error_text, f"Syntax error in spatial expression {name}: {expression}"
            assert "parse" not in error_text, f"Parse error in spatial expression {name}: {expression}"

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_ogc_compliance_score_calculation(
        self, http_client: httpx.Client
    ):
        """Calculate overall OGC compliance score from capabilities."""
        response = _wfs_get_capabilities(http_client)
        assert response.status_code == 200

        root = ET.fromstring(response.content)
        ns = {"fes": "http://www.opengis.net/fes/2.0"}

        # Calculate compliance score
        total_checks = 0
        passed_checks = 0

        # Core conformance classes (8 checks)
        total_checks += 8
        constraints = root.findall(".//fes:Constraint", ns)
        constraint_names = {c.attrib["name"] for c in constraints}

        core_constraints = {
            "ImplementsQuery", "ImplementsAdHocQuery", "ImplementsResourceId",
            "ImplementsStandardFilter", "ImplementsMinSpatialFilter",
            "ImplementsSpatialFilter", "ImplementsMinTemporalFilter", "ImplementsTemporalFilter"
        }
        passed_checks += len(core_constraints.intersection(constraint_names))

        # Temporal operator consistency (2 checks)
        total_checks += 2
        temporal_ops = root.findall(".//fes:TemporalOperator", ns)
        temporal_names = {op.attrib["name"] for op in temporal_ops}
        constraint_defaults = _constraint_defaults(root)
        temporal_conformance_enabled = constraint_defaults.get("ImplementsTemporalFilter") == "TRUE"
        if temporal_conformance_enabled and len(temporal_ops) >= 10:
            passed_checks += 1
        if temporal_conformance_enabled and SCHEMA_VALID_FES_TEMPORAL_OPERATORS.issubset(temporal_names):
            passed_checks += 1
        if not temporal_conformance_enabled and len(temporal_ops) == 0:
            passed_checks += 2

        # Spatial operator coverage (2 checks)
        total_checks += 2
        spatial_ops = root.findall(".//fes:SpatialOperator", ns)
        if len(spatial_ops) >= 8:
            passed_checks += 1
        if len(spatial_ops) >= 12:
            passed_checks += 1

        # FES function advertisement consistency (2 checks)
        total_checks += 2
        functions = root.findall(".//fes:Function", ns)
        if constraint_defaults.get("ImplementsFunctions") == "FALSE" and len(functions) == 0:
            passed_checks += 1
        if constraint_defaults.get("ImplementsArithmeticOperators") == "FALSE":
            passed_checks += 1

        # CQL2 support (3 checks)
        total_checks += 3
        cql2_constraints = {"ImplementsCQL2Text", "ImplementsCQL2JSON", "ImplementsCQL2SpatialOperators"}
        passed_checks += len(cql2_constraints.intersection(constraint_names))

        # Enhanced features (3 checks)
        total_checks += 3
        enhanced_constraints = {"ImplementsFunctions", "ImplementsArithmeticOperators", "ImplementsExtendedOperators"}
        passed_checks += len(enhanced_constraints.intersection(constraint_names))

        compliance_score = passed_checks / total_checks

        # Should achieve 95% compliance target
        assert compliance_score >= 0.95, f"Compliance score {compliance_score:.2%} should be >= 95%"
