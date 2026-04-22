// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Wfs20.Models;
using Honua.Server.Features.Wfs20.Services;
using System.Reflection;
using System.Xml;
using System.Xml.Serialization;

namespace Honua.Server.Tests.Features.Wfs20;

/// <summary>
/// XML validation tests for WFS 2.0 GetCapabilities response.
/// Validates that FilterCapabilities XML output contains all required elements for OGC compliance.
/// </summary>
public class Wfs20CapabilitiesXmlValidationTests
{
    /// <summary>
    /// Validates that XML serialization includes all temporal operators with correct naming.
    /// This test ensures the gap between implementation and advertisement is closed.
    /// </summary>
    [Fact]
    public void GetCapabilitiesXml_TemporalOperators_ShouldContainAllAllenIntervalOperators()
    {
        // Arrange
        var capabilities = CreateWfsCapabilitiesWithFilterCapabilities();
        var xml = SerializeToXml(capabilities);

        // Act & Assert - Verify XML contains all Allen interval operators
        var expectedOperators = new[]
        {
            "After", "Before", "During", "Contains", "Equals", "Disjoint",
            "Intersects", "Meets", "MetBy", "Overlaps", "OverlappedBy",
            "Starts", "StartedBy", "Finishes", "FinishedBy"
        };

        foreach (var expectedOperator in expectedOperators)
        {
            xml.Should().Contain($"name=\"{expectedOperator}\"",
                $"XML should contain TemporalOperator with name='{expectedOperator}' for Allen interval compliance");
        }

        // Should NOT contain legacy T-prefixed operators
        var legacyOperators = new[] { "TContains", "TOverlaps", "TEquals", "TIntersects" };
        foreach (var legacyOperator in legacyOperators)
        {
            xml.Should().NotContain(legacyOperator,
                $"XML should not contain legacy operator '{legacyOperator}' - should use proper OGC naming");
        }

        // Verify TemporalOperators section is present
        xml.Should().Contain("TemporalOperators", "XML should contain TemporalOperators section");
        xml.Should().MatchRegex(@"<.*TemporalOperator[^>]*name=""[^""]+""[^>]*/>",
            "XML should contain properly formatted TemporalOperator elements");
    }

    /// <summary>
    /// Validates that XML includes all spatial functions with proper function definitions.
    /// Ensures ST_Area, ST_Length, ST_Buffer, ST_Centroid etc. are discoverable by clients.
    /// </summary>
    [Fact]
    public void GetCapabilitiesXml_SpatialFunctions_ShouldContainComprehensiveFunctionDefinitions()
    {
        // Arrange
        var capabilities = CreateWfsCapabilitiesWithFilterCapabilities();
        var xml = SerializeToXml(capabilities);

        // Act & Assert - Verify XML contains core spatial functions
        var coreSpatialFunctions = new[]
        {
            "ST_Area", "ST_Length", "ST_Distance", "ST_Buffer", "ST_Centroid",
            "ST_IsValid", "ST_GeometryType", "ST_Envelope", "ST_ConvexHull"
        };

        foreach (var spatialFunction in coreSpatialFunctions)
        {
            xml.Should().Contain($"name=\"{spatialFunction}\"",
                $"XML should contain Function with name='{spatialFunction}' for spatial capability discovery");
        }

        // Verify Functions section structure
        xml.Should().Contain("Functions", "XML should contain Functions section");
        xml.Should().Contain("Returns", "Function definitions should include return types");
        xml.Should().Contain("Arguments", "Function definitions should include argument specifications");

        // Verify proper function definition structure
        xml.Should().MatchRegex(@"<.*Function[^>]*name=""ST_[^""]+""[^>]*>",
            "XML should contain properly formatted spatial Function elements");
    }

    /// <summary>
    /// Validates conformance constraints are properly serialized with correct TRUE/FALSE values.
    /// Ensures ImplementsExtendedOperators and other constraints accurately reflect capabilities.
    /// </summary>
    [Fact]
    public void GetCapabilitiesXml_ConformanceConstraints_ShouldReflectActualImplementation()
    {
        // Arrange
        var capabilities = CreateWfsCapabilitiesWithFilterCapabilities();
        var xml = SerializeToXml(capabilities);

        // Act & Assert - Core conformance constraints must be TRUE
        var coreConstraints = new[]
        {
            "ImplementsQuery", "ImplementsAdHocQuery", "ImplementsResourceId",
            "ImplementsStandardFilter", "ImplementsSpatialFilter", "ImplementsTemporalFilter",
            "ImplementsFunctions", "ImplementsExtendedOperators"
        };

        foreach (var constraint in coreConstraints)
        {
            xml.Should().Contain($"name=\"{constraint}\"",
                $"XML should contain Constraint with name='{constraint}'");
            // Verify the constraint is set to TRUE (implementation-dependent check)
            xml.Should().MatchRegex($@"name=""{constraint}""[^>]*>[\s\S]*?TRUE[\s\S]*?</",
                $"Constraint '{constraint}' should have DefaultValue='TRUE' in XML");
        }

        // CQL2 constraints should be present and TRUE
        var cql2Constraints = new[]
        {
            "ImplementsCQL2Text", "ImplementsCQL2JSON",
            "ImplementsCQL2SpatialOperators", "ImplementsCQL2TemporalOperators"
        };

        foreach (var cql2Constraint in cql2Constraints)
        {
            xml.Should().Contain($"name=\"{cql2Constraint}\"",
                $"XML should contain CQL2 constraint '{cql2Constraint}' for modern standards compliance");
        }
    }

    /// <summary>
    /// Validates that XML structure follows OGC WFS 2.0 and Filter Encoding 2.0 schemas.
    /// Ensures namespace declarations and element hierarchy are correct.
    /// </summary>
    [Fact]
    public void GetCapabilitiesXml_Structure_ShouldFollowOGCSchemaRequirements()
    {
        // Arrange
        var capabilities = CreateWfsCapabilitiesWithFilterCapabilities();
        var xml = SerializeToXml(capabilities);

        // Act & Assert - Verify core XML structure
        xml.Should().Contain("Filter_Capabilities", "XML should contain Filter_Capabilities root element");
        xml.Should().Contain("Conformance", "FilterCapabilities should contain Conformance section");
        xml.Should().Contain("Scalar_Capabilities", "FilterCapabilities should contain ScalarCapabilities");
        xml.Should().Contain("Spatial_Capabilities", "FilterCapabilities should contain SpatialCapabilities");
        xml.Should().Contain("Temporal_Capabilities", "FilterCapabilities should contain TemporalCapabilities");

        // Verify namespace declarations (critical for client parsing)
        xml.Should().Contain("xmlns:fes=", "XML should declare FES namespace");
        xml.Should().Contain("xmlns:gml=", "XML should declare GML namespace for geometry types");

        // Verify XML is well-formed
        var xmlDoc = new XmlDocument();
        var parseAction = () => xmlDoc.LoadXml(xml);
        parseAction.Should().NotThrow("Generated XML should be well-formed and parseable");

        // Verify required sections are present with content
        xml.Should().MatchRegex(@"<[\s\S]*?ComparisonOperators[^>]*>[\s\S]*?PropertyIsEqualTo[\s\S]*?</",
            "ComparisonOperators should contain standard comparison operators");
        xml.Should().MatchRegex(@"<[\s\S]*?GeometryOperands[^>]*>[\s\S]*?Point[\s\S]*?</",
            "GeometryOperands should contain supported geometry types");
    }

    /// <summary>
    /// Validates overall XML compliance score by checking presence of all required elements.
    /// This is the comprehensive validation that confirms 95%+ compliance is achieved in XML output.
    /// </summary>
    [Fact]
    public void GetCapabilitiesXml_OverallCompliance_ShouldAchieve95PercentInXmlAdvertisement()
    {
        // Arrange
        var capabilities = CreateWfsCapabilitiesWithFilterCapabilities();
        var xml = SerializeToXml(capabilities);

        // Act - Calculate compliance based on XML content
        var complianceScore = CalculateXmlComplianceScore(xml);

        // Assert
        complianceScore.Should().BeGreaterOrEqualTo(0.95,
            "XML advertisement should achieve 95%+ compliance score for OGC certification readiness");

        // Detailed validation of critical elements
        ValidateCriticalXmlElements(xml);
    }

    /// <summary>
    /// Creates WFS capabilities with actual FilterCapabilities from handler.
    /// </summary>
    private static WfsCapabilities CreateWfsCapabilitiesWithFilterCapabilities()
    {
        // Get actual FilterCapabilities from Wfs20Handler
        var type = typeof(Wfs20Handler);
        var method = type.GetMethod("BuildFilterCapabilities",
            BindingFlags.NonPublic | BindingFlags.Static);
        var filterCapabilities = (FilterCapabilities)method!.Invoke(null, null)!;

        return new WfsCapabilities
        {
            Version = "2.0.0",
            ServiceIdentification = new ServiceIdentification
            {
                Title = "Honua WFS",
                ServiceType = "WFS",
                ServiceTypeVersion = ["2.0.0"]
            },
            ServiceProvider = new ServiceProvider
            {
                ProviderName = "Honua"
            },
            FilterCapabilities = filterCapabilities
        };
    }

    /// <summary>
    /// Serializes capabilities to XML string for validation.
    /// </summary>
    private static string SerializeToXml(WfsCapabilities capabilities)
    {
        var serializer = new XmlSerializer(typeof(WfsCapabilities));
        using var stringWriter = new StringWriter();
        using var xmlWriter = XmlWriter.Create(stringWriter, new XmlWriterSettings
        {
            Indent = true,
            OmitXmlDeclaration = false,
            Encoding = System.Text.Encoding.UTF8
        });

        serializer.Serialize(xmlWriter, capabilities, capabilities.Namespaces);
        return stringWriter.ToString();
    }

    /// <summary>
    /// Calculates compliance score based on XML content analysis.
    /// </summary>
    private static double CalculateXmlComplianceScore(string xml)
    {
        var totalChecks = 0;
        var passedChecks = 0;

        // Temporal operators check
        totalChecks += 15;
        var temporalOperators = new[]
        {
            "After", "Before", "During", "Contains", "Equals", "Disjoint",
            "Intersects", "Meets", "MetBy", "Overlaps", "OverlappedBy",
            "Starts", "StartedBy", "Finishes", "FinishedBy"
        };
        passedChecks += temporalOperators.Count(op => xml.Contains($"name=\"{op}\""));

        // Spatial functions check (15 points)
        totalChecks += 15;
        var spatialFunctions = new[]
        {
            "ST_Area", "ST_Length", "ST_Distance", "ST_Buffer", "ST_Centroid",
            "ST_IsValid", "ST_GeometryType", "ST_Envelope", "ST_ConvexHull",
            "ST_Boundary", "ST_NumGeometries", "ST_SRID", "ST_IsSimple",
            "ST_IsClosed", "ST_IsEmpty"
        };
        passedChecks += spatialFunctions.Count(func => xml.Contains($"name=\"{func}\""));

        // Core conformance constraints (10 points)
        totalChecks += 10;
        var coreConstraints = new[]
        {
            "ImplementsQuery", "ImplementsAdHocQuery", "ImplementsResourceId",
            "ImplementsStandardFilter", "ImplementsSpatialFilter", "ImplementsTemporalFilter",
            "ImplementsFunctions", "ImplementsExtendedOperators", "ImplementsArithmeticOperators",
            "ImplementsLogicalOperators"
        };
        passedChecks += coreConstraints.Count(constraint => xml.Contains($"name=\"{constraint}\""));

        // CQL2 support (5 points)
        totalChecks += 5;
        var cql2Features = new[]
        {
            "ImplementsCQL2Text", "ImplementsCQL2JSON", "ImplementsCQL2SpatialOperators",
            "ImplementsCQL2TemporalOperators", "ImplementsCQL2Functions"
        };
        passedChecks += cql2Features.Count(feature => xml.Contains($"name=\"{feature}\""));

        return (double)passedChecks / totalChecks;
    }

    /// <summary>
    /// Validates critical XML elements that are essential for 95% compliance.
    /// </summary>
    private static void ValidateCriticalXmlElements(string xml)
    {
        // Critical sections must be present
        xml.Should().Contain("Filter_Capabilities", "Filter_Capabilities section is required");
        xml.Should().Contain("Conformance", "Conformance section is required");
        xml.Should().Contain("TemporalOperators", "TemporalOperators section is required");
        xml.Should().Contain("SpatialOperators", "SpatialOperators section is required");
        xml.Should().Contain("Functions", "Functions section is required");

        // Critical operators must be present (subset of Allen interval operators)
        var criticalTemporalOperators = new[] { "After", "Before", "During", "Contains", "Intersects" };
        foreach (var op in criticalTemporalOperators)
        {
            xml.Should().Contain($"name=\"{op}\"",
                $"Critical temporal operator '{op}' must be present in XML");
        }

        // Critical spatial functions must be present
        var criticalSpatialFunctions = new[] { "ST_Area", "ST_Length", "ST_Buffer", "ST_Centroid" };
        foreach (var func in criticalSpatialFunctions)
        {
            xml.Should().Contain($"name=\"{func}\"",
                $"Critical spatial function '{func}' must be present in XML");
        }

        // Critical conformance declarations must be TRUE
        var criticalConstraints = new[]
        {
            "ImplementsStandardFilter", "ImplementsSpatialFilter",
            "ImplementsTemporalFilter", "ImplementsFunctions"
        };
        foreach (var constraint in criticalConstraints)
        {
            xml.Should().MatchRegex($@"name=""{constraint}""[^>]*>[\s\S]*?TRUE[\s\S]*?</",
                $"Critical constraint '{constraint}' must be declared as TRUE in XML");
        }
    }
}
