// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Wfs20.Models;
using System.Xml;
using System.Xml.Serialization;

namespace Honua.Server.Tests.Features.Wfs20;

/// <summary>
/// Integration tests for WFS 2.0 standards compliance
/// </summary>
public class Wfs20StandardsComplianceIntegrationTests
{
    /// <summary>
    /// Validates that WFS GetCapabilities response includes enhanced FilterCapabilities
    /// and maintains XML schema compliance
    /// </summary>
    [Fact]
    public void WfsCapabilities_WithEnhancedFilterCapabilities_ShouldSerializeToValidXml()
    {
        // Arrange
        var capabilities = CreateCompleteWfsCapabilities();

        // Act
        var xml = SerializeCapabilities(capabilities);

        // Assert
        xml.Should().NotBeEmpty();

        // Verify key elements are present
        xml.Should().Contain("Filter_Capabilities");
        xml.Should().Contain("Conformance");
        xml.Should().Contain("TemporalOperators");
        xml.Should().Contain("SpatialOperators");
        xml.Should().Contain("ComparisonOperators");
        xml.Should().Contain("Functions");

        // Verify specific enhanced operators are advertised (proper OGC naming, no T- prefix)
        xml.Should().Contain("Contains");
        xml.Should().Contain("Overlaps");
        xml.Should().Contain("ST_Area");
        xml.Should().Contain("ST_Distance");
        xml.Should().Contain("ImplementsCQL2Text");

        // Ensure XML is well-formed
        var xmlDoc = new XmlDocument();
        var action = () => xmlDoc.LoadXml(xml);
        action.Should().NotThrow("Serialized XML should be well-formed");
    }

    /// <summary>
    /// Validates that all advertised temporal operators are correctly named per OGC spec
    /// </summary>
    [Fact]
    public void FilterCapabilities_TemporalOperators_ShouldFollowOGCNaming()
    {
        // Arrange
        var capabilities = CreateCompleteWfsCapabilities();
        var temporalOps = capabilities.FilterCapabilities!.TemporalCapabilities!
            .TemporalOperators!.Operators;

        // Act & Assert
        foreach (var op in temporalOps)
        {
            // All temporal operators should be proper case
            op.Name.Should().MatchRegex(@"^[A-Z][a-zA-Z]*$",
                $"Temporal operator '{op.Name}' should be CapitalCase");

            // Should not use legacy T-prefixed naming
            op.Name.Should().NotStartWith("T_",
                $"Temporal operator '{op.Name}' should not use T_ prefix");
        }

        // Verify all 14 Allen interval operators are present
        var operatorNames = temporalOps.Select(op => op.Name).ToHashSet();
        var expectedOperators = new[]
        {
            "After", "Before", "During", "Contains", "Equals", "Disjoint",
            "Intersects", "Meets", "MetBy", "Overlaps", "OverlappedBy",
            "Starts", "StartedBy", "Finishes", "FinishedBy"
        };

        foreach (var expected in expectedOperators)
        {
            operatorNames.Should().Contain(expected,
                $"Should include standard temporal operator '{expected}'");
        }
    }

    /// <summary>
    /// Validates that conformance constraints meet OGC Filter Encoding 2.0 requirements
    /// </summary>
    [Fact]
    public void FilterCapabilities_ConformanceConstraints_ShouldMeetOGCRequirements()
    {
        // Arrange
        var capabilities = CreateCompleteWfsCapabilities();
        var constraints = capabilities.FilterCapabilities!.Conformance.Constraints;
        var constraintNames = constraints.Select(c => c.Name).ToHashSet();

        // Act & Assert - Core OGC Filter Encoding 2.0 conformance classes
        var requiredConstraints = new[]
        {
            "ImplementsQuery",
            "ImplementsAdHocQuery",
            "ImplementsResourceId",
            "ImplementsStandardFilter",
            "ImplementsMinSpatialFilter",
            "ImplementsSpatialFilter",
            "ImplementsMinTemporalFilter",
            "ImplementsTemporalFilter"
        };

        foreach (var required in requiredConstraints)
        {
            constraintNames.Should().Contain(required,
                $"Must implement core OGC conformance class '{required}'");

            var constraint = constraints.First(c => c.Name == required);
            constraint.DefaultValue.Should().Be("TRUE",
                $"Core conformance class '{required}' should be enabled");
        }

        // Enhanced capabilities should be advertised
        var enhancedConstraints = new[]
        {
            "ImplementsFunctions",
            "ImplementsExtendedOperators",
            "ImplementsCQL2Text",
            "ImplementsCQL2JSON",
            "ImplementsCQL2SpatialOperators",
            "ImplementsCQL2TemporalOperators"
        };

        foreach (var enhanced in enhancedConstraints)
        {
            constraintNames.Should().Contain(enhanced,
                $"Should advertise enhanced capability '{enhanced}'");
        }
    }

    /// <summary>
    /// Validates function definitions meet OGC Function model requirements
    /// </summary>
    [Fact]
    public void FilterCapabilities_FunctionDefinitions_ShouldMeetOGCStandards()
    {
        // Arrange
        var capabilities = CreateCompleteWfsCapabilities();
        var functions = capabilities.FilterCapabilities!.Functions!.Functions;

        // Act & Assert
        functions.Should().NotBeEmpty("Should advertise available functions");

        foreach (var func in functions)
        {
            // Basic function definition requirements
            func.Name.Should().NotBeNullOrWhiteSpace("Function must have name");
            func.Returns.Should().NotBeNull($"Function '{func.Name}' must specify return type");
            func.Returns!.Type.Should().NotBeNullOrWhiteSpace(
                $"Function '{func.Name}' must have valid return type");

            // Return type should be standard OGC type
            var validTypes = new[] { "string", "number", "integer", "boolean", "date", "geometry", "any" };
            validTypes.Should().Contain(func.Returns.Type,
                $"Function '{func.Name}' return type '{func.Returns.Type}' should be standard OGC type");

            // Validate argument types if present
            if (func.Arguments?.Arguments != null)
            {
                foreach (var arg in func.Arguments.Arguments)
                {
                    arg.Name.Should().NotBeNullOrWhiteSpace(
                        $"Function '{func.Name}' argument must have name");
                    validTypes.Should().Contain(arg.Type,
                        $"Function '{func.Name}' argument '{arg.Name}' type '{arg.Type}' should be standard OGC type");
                }
            }
        }

        // Should include key function categories for comprehensive compliance
        var functionNames = functions.Select(f => f.Name.ToUpperInvariant()).ToHashSet();

        // String functions
        functionNames.Should().Contain("UPPER");
        functionNames.Should().Contain("LOWER");
        functionNames.Should().Contain("CONCAT");

        // Math functions
        functionNames.Should().Contain("ABS");
        functionNames.Should().Contain("SQRT");
        functionNames.Should().Contain("POWER");

        // Spatial functions
        functionNames.Should().Contain("ST_AREA");
        functionNames.Should().Contain("ST_DISTANCE");
        functionNames.Should().Contain("ST_BUFFER");

        // Temporal functions
        functionNames.Should().Contain("YEAR");
        functionNames.Should().Contain("MONTH");
        functionNames.Should().Contain("NOW");

        // Aggregate functions
        functionNames.Should().Contain("COUNT");
        functionNames.Should().Contain("SUM");
        functionNames.Should().Contain("AVG");
    }

    /// <summary>
    /// Validates that spatial capabilities include comprehensive geometry support
    /// </summary>
    [Fact]
    public void FilterCapabilities_SpatialCapabilities_ShouldSupportFullGeometryModel()
    {
        // Arrange
        var capabilities = CreateCompleteWfsCapabilities();
        var spatialCaps = capabilities.FilterCapabilities!.SpatialCapabilities!;

        // Act & Assert
        var geometryOperands = spatialCaps.GeometryOperands!.Operands
            .Select(op => op.Name.Name).ToHashSet();

        // Should support all core geometry types
        var coreGeometryTypes = new[]
        {
            "Point", "LineString", "Polygon", "Envelope"
        };

        foreach (var coreType in coreGeometryTypes)
        {
            geometryOperands.Should().Contain(coreType,
                $"Should support core geometry type '{coreType}'");
        }

        // Should support multi-geometry types for comprehensive compliance
        var multiGeometryTypes = new[]
        {
            "MultiPoint", "MultiLineString", "MultiPolygon", "GeometryCollection"
        };

        foreach (var multiType in multiGeometryTypes)
        {
            geometryOperands.Should().Contain(multiType,
                $"Should support multi-geometry type '{multiType}'");
        }

        // Spatial operators should include all DE-9IM based operators
        var spatialOperators = spatialCaps.SpatialOperators!.Operators
            .Select(op => op.Name).ToHashSet();

        var de9imOperators = new[]
        {
            "Intersects", "Contains", "Within", "Crosses", "Touches",
            "Overlaps", "Disjoint", "Equals"
        };

        foreach (var de9imOp in de9imOperators)
        {
            spatialOperators.Should().Contain(de9imOp,
                $"Should support DE-9IM operator '{de9imOp}'");
        }

        // Should include distance-based operators
        spatialOperators.Should().Contain("DWithin");
        spatialOperators.Should().Contain("Beyond");
        spatialOperators.Should().Contain("BBOX");
    }

    /// <summary>
    /// Calculates overall OGC compliance score based on implemented features
    /// </summary>
    [Fact]
    public void FilterCapabilities_OverallCompliance_ShouldAchieve95PercentTarget()
    {
        // Arrange
        var capabilities = CreateCompleteWfsCapabilities();
        var compliance = CalculateOGCComplianceScore(capabilities.FilterCapabilities!);

        // Act & Assert
        compliance.Should().BeGreaterThan(0.95,
            "Enhanced filter capabilities should achieve 95% OGC compliance target");
    }

    private static WfsCapabilities CreateCompleteWfsCapabilities()
    {
        // Use reflection to get the enhanced FilterCapabilities
        var type = typeof(Wfs20Handler);
        var method = type.GetMethod("BuildFilterCapabilities",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var filterCapabilities = (FilterCapabilities)method!.Invoke(null, null)!;

        return new WfsCapabilities
        {
            ServiceIdentification = new ServiceIdentification(),
            ServiceProvider = new ServiceProvider(),
            FilterCapabilities = filterCapabilities
        };
    }

    private static string SerializeCapabilities(WfsCapabilities capabilities)
    {
        var serializer = new XmlSerializer(typeof(WfsCapabilities));
        using var stringWriter = new StringWriter();
        using var xmlWriter = XmlWriter.Create(stringWriter, new XmlWriterSettings
        {
            Indent = true,
            OmitXmlDeclaration = false,
            Encoding = System.Text.Encoding.UTF8
        });

        serializer.Serialize(xmlWriter, capabilities);
        return stringWriter.ToString();
    }

    private static double CalculateOGCComplianceScore(FilterCapabilities capabilities)
    {
        var totalChecks = 0;
        var passedChecks = 0;

        // Core filter capabilities
        totalChecks += 8;
        var constraints = capabilities.Conformance.Constraints.Select(c => c.Name).ToHashSet();
        if (constraints.Contains("ImplementsQuery")) passedChecks++;
        if (constraints.Contains("ImplementsAdHocQuery")) passedChecks++;
        if (constraints.Contains("ImplementsResourceId")) passedChecks++;
        if (constraints.Contains("ImplementsStandardFilter")) passedChecks++;
        if (constraints.Contains("ImplementsMinSpatialFilter")) passedChecks++;
        if (constraints.Contains("ImplementsSpatialFilter")) passedChecks++;
        if (constraints.Contains("ImplementsMinTemporalFilter")) passedChecks++;
        if (constraints.Contains("ImplementsTemporalFilter")) passedChecks++;

        // Temporal operators (should have all 15)
        totalChecks += 2;
        var temporalOps = capabilities.TemporalCapabilities?.TemporalOperators?.Operators?.Length ?? 0;
        if (temporalOps >= 10) passedChecks++; // Good coverage
        if (temporalOps >= 15) passedChecks++; // Full coverage

        // Spatial operators (should have comprehensive set)
        totalChecks += 2;
        var spatialOps = capabilities.SpatialCapabilities?.SpatialOperators?.Operators?.Length ?? 0;
        if (spatialOps >= 8) passedChecks++; // Basic DE-9IM
        if (spatialOps >= 12) passedChecks++; // Full set

        // Function support
        totalChecks += 2;
        var functions = capabilities.Functions?.Functions?.Length ?? 0;
        if (functions >= 20) passedChecks++; // Good function coverage
        if (functions >= 35) passedChecks++; // Comprehensive coverage

        // CQL2 support
        totalChecks += 3;
        if (constraints.Contains("ImplementsCQL2Text")) passedChecks++;
        if (constraints.Contains("ImplementsCQL2JSON")) passedChecks++;
        if (constraints.Contains("ImplementsCQL2SpatialOperators")) passedChecks++;

        // Enhanced features
        totalChecks += 3;
        if (constraints.Contains("ImplementsFunctions")) passedChecks++;
        if (constraints.Contains("ImplementsArithmeticOperators")) passedChecks++;
        if (constraints.Contains("ImplementsExtendedOperators")) passedChecks++;

        return (double)passedChecks / totalChecks;
    }
}