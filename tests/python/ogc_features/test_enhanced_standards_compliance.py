# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Enhanced OGC standards compliance tests for WFS 2.0 filter capabilities.
"""

import pytest
import httpx
from xml.etree import ElementTree as ET

from shared.temporal_operator_compliance import ALL_TEMPORAL_COMPLIANCE_CASES
from shared.spatial_function_compliance import ALL_SPATIAL_FUNCTION_CASES


class TestEnhancedStandardsCompliance:
    """Tests for enhanced OGC standards compliance in WFS 2.0 implementation."""

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_wfs_getcapabilities_enhanced_filter_capabilities(
        self, http_client: httpx.Client
    ):
        """Test that WFS GetCapabilities returns enhanced FilterCapabilities."""
        response = http_client.get("/wfs", params={"request": "GetCapabilities"})
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
    def test_wfs_enhanced_temporal_operators_advertised(
        self, http_client: httpx.Client
    ):
        """Test that all enhanced temporal operators are advertised in capabilities."""
        response = http_client.get("/wfs", params={"request": "GetCapabilities"})
        assert response.status_code == 200

        root = ET.fromstring(response.content)
        ns = {"fes": "http://www.opengis.net/fes/2.0"}

        # Find temporal operators
        temporal_ops = root.findall(".//fes:TemporalOperator", ns)
        advertised_ops = {op.attrib["name"] for op in temporal_ops}

        # Verify all Allen interval operators are present
        expected_ops = {
            "After", "Before", "During", "Contains", "Equals", "Disjoint",
            "Intersects", "Meets", "MetBy", "Overlaps", "OverlappedBy",
            "Starts", "StartedBy", "Finishes", "FinishedBy"
        }

        for op in expected_ops:
            assert op in advertised_ops, f"Missing temporal operator: {op}"

        # Should have at least 15 temporal operators for full Allen compliance
        assert len(advertised_ops) >= 15, f"Should advertise at least 15 temporal operators, got {len(advertised_ops)}"

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_wfs_enhanced_spatial_functions_advertised(
        self, http_client: httpx.Client
    ):
        """Test that spatial functions are advertised in capabilities."""
        response = http_client.get("/wfs", params={"request": "GetCapabilities"})
        assert response.status_code == 200

        root = ET.fromstring(response.content)
        ns = {"fes": "http://www.opengis.net/fes/2.0"}

        # Find function definitions
        functions = root.findall(".//fes:Function", ns)
        advertised_functions = {func.attrib["name"] for func in functions}

        # Verify spatial functions are advertised
        expected_spatial_functions = {
            "ST_Area", "ST_Length", "ST_Distance", "ST_Buffer", "ST_Centroid",
            "ST_IsValid", "ST_GeometryType", "ST_NumGeometries"
        }

        for func in expected_spatial_functions:
            assert func in advertised_functions, f"Missing spatial function: {func}"

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_wfs_comprehensive_function_coverage(
        self, http_client: httpx.Client
    ):
        """Test comprehensive function coverage across all categories."""
        response = http_client.get("/wfs", params={"request": "GetCapabilities"})
        assert response.status_code == 200

        root = ET.fromstring(response.content)
        ns = {"fes": "http://www.opengis.net/fes/2.0"}

        functions = root.findall(".//fes:Function", ns)
        advertised_functions = {func.attrib["name"] for func in functions}

        # String functions
        string_functions = {"UPPER", "LOWER", "CONCAT", "LENGTH", "SUBSTRING"}
        assert string_functions.issubset(advertised_functions), "Missing string functions"

        # Math functions
        math_functions = {"ABS", "CEIL", "FLOOR", "ROUND", "SQRT", "POWER", "MOD"}
        assert math_functions.issubset(advertised_functions), "Missing math functions"

        # Date/time functions
        date_functions = {"YEAR", "MONTH", "DAY", "NOW"}
        assert date_functions.issubset(advertised_functions), "Missing date/time functions"

        # Aggregate functions
        aggregate_functions = {"COUNT", "SUM", "AVG", "MIN", "MAX"}
        assert aggregate_functions.issubset(advertised_functions), "Missing aggregate functions"

        # Should have substantial function coverage (35+ functions)
        assert len(advertised_functions) >= 35, f"Should advertise 35+ functions, got {len(advertised_functions)}"

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
        response = http_client.get("/wfs", params={"request": "GetCapabilities"})
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

        # Temporal operator coverage (2 checks)
        total_checks += 2
        temporal_ops = root.findall(".//fes:TemporalOperator", ns)
        if len(temporal_ops) >= 10:
            passed_checks += 1
        if len(temporal_ops) >= 15:
            passed_checks += 1

        # Spatial operator coverage (2 checks)
        total_checks += 2
        spatial_ops = root.findall(".//fes:SpatialOperator", ns)
        if len(spatial_ops) >= 8:
            passed_checks += 1
        if len(spatial_ops) >= 12:
            passed_checks += 1

        # Function coverage (2 checks)
        total_checks += 2
        functions = root.findall(".//fes:Function", ns)
        if len(functions) >= 20:
            passed_checks += 1
        if len(functions) >= 35:
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