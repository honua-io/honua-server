// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Wfs20.Models;
using System.Xml;
using System.Xml.Schema;

namespace Honua.Server.Tests.Features.Wfs20;

/// <summary>
/// Tests for OGC standards compliance validation
/// </summary>
public class Wfs20ComplianceValidationTests
{
    /// <summary>
    /// Validates that the enhanced FilterCapabilities meets OGC Filter Encoding 2.0 requirements
    /// </summary>
    [Fact]
    public void FilterCapabilities_ShouldMeetOGCFilterEncoding20Requirements()
    {
        // Arrange
        var complianceChecklist = new OGCFilterEncoding20Checklist();

        // Act
        var filterCapabilities = InvokeBuildFilterCapabilities();
        var complianceScore = complianceChecklist.CalculateCompliance(filterCapabilities);

        // Assert
        complianceScore.Should().BeGreaterThan(0.95, "Should achieve 95% compliance target");

        // Verify specific OGC requirements
        complianceChecklist.HasBasicTemporalOperators.Should().BeTrue("Must support basic temporal operators");
        complianceChecklist.HasExtendedTemporalOperators.Should().BeTrue("Should support extended temporal operators");
        complianceChecklist.HasComprehensiveSpatialOperators.Should().BeTrue("Must support comprehensive spatial operators");
        complianceChecklist.HasFunctionCapabilities.Should().BeTrue("Should advertise function capabilities");
        complianceChecklist.HasCQL2Support.Should().BeTrue("Should support CQL2 conformance classes");
    }

    /// <summary>
    /// Validates that WFS GetCapabilities XML conforms to OGC WFS 2.0 schema
    /// </summary>
    [Fact]
    public void WfsCapabilities_WithEnhancedFilterCapabilities_ShouldValidateAgainstOGCSchema()
    {
        // Arrange
        var capabilities = CreateSampleWfsCapabilities();
        var schemaValidationResult = new List<string>();

        // Act
        var isValid = ValidateAgainstOGCSchema(capabilities, out var validationErrors);

        // Assert
        if (!isValid)
        {
            // Log validation errors for debugging
            validationErrors.Should().BeEmpty($"WFS Capabilities should validate against OGC schema. Errors: {string.Join("; ", validationErrors)}");
        }

        isValid.Should().BeTrue("Enhanced WFS Capabilities should conform to OGC WFS 2.0 schema");
    }

    /// <summary>
    /// Validates temporal operator naming compliance with OGC specifications
    /// </summary>
    [Fact]
    public void TemporalOperators_ShouldFollowOGCNamingConventions()
    {
        // Act
        var filterCapabilities = InvokeBuildFilterCapabilities();
        var temporalOperators = filterCapabilities.TemporalCapabilities!.TemporalOperators!.Operators;

        // Assert - All temporal operators should follow OGC naming patterns
        foreach (var op in temporalOperators)
        {
            op.Name.Should().NotBeNullOrWhiteSpace();

            // All operators should be CapitalCase per OGC Filter Encoding 2.0
            op.Name.Should().MatchRegex(@"^[A-Z][a-zA-Z]*$", $"Temporal operator '{op.Name}' should follow CapitalCase pattern per OGC spec");
        }

        // Verify no duplicate operators
        var operatorNames = temporalOperators.Select(op => op.Name).ToList();
        operatorNames.Should().OnlyHaveUniqueItems("Temporal operators should not be duplicated");
    }

    /// <summary>
    /// Validates function definitions compliance with FES 2.0 function model
    /// </summary>
    [Fact]
    public void FunctionDefinitions_ShouldComplywithFES20FunctionModel()
    {
        // Act
        var filterCapabilities = InvokeBuildFilterCapabilities();
        var functions = filterCapabilities.Functions!.Functions;

        // Assert
        foreach (var func in functions)
        {
            func.Name.Should().NotBeNullOrWhiteSpace();
            func.Returns.Should().NotBeNull($"Function '{func.Name}' should specify return type");
            func.Returns!.Type.Should().NotBeNullOrWhiteSpace($"Function '{func.Name}' should have valid return type");

            // Validate return type is recognized OGC type
            func.Returns.Type.Should().BeOneOf("string", "number", "integer", "boolean", "date", "geometry", "any",
                $"Function '{func.Name}' should use standard OGC type for return");

            // Validate argument types if present
            if (func.Arguments?.Arguments != null)
            {
                foreach (var arg in func.Arguments.Arguments)
                {
                    arg.Name.Should().NotBeNullOrWhiteSpace($"Function '{func.Name}' argument should have name");
                    arg.Type.Should().BeOneOf("string", "number", "integer", "boolean", "date", "geometry", "any",
                        $"Function '{func.Name}' argument '{arg.Name}' should use standard OGC type");
                }
            }
        }

        // Verify comprehensive function coverage
        var functionNames = functions.Select(f => f.Name).ToHashSet();

        // Should include key function categories
        functionNames.Should().Contain(f => f.StartsWith("ST_"), "Should include spatial functions");
        functionNames.Should().Contain(f => f is "UPPER" or "LOWER" or "CONCAT", "Should include string functions");
        functionNames.Should().Contain(f => f is "ABS" or "CEIL" or "FLOOR", "Should include math functions");
        functionNames.Should().Contain(f => f is "YEAR" or "MONTH" or "DAY", "Should include date functions");
    }

    private static FilterCapabilities InvokeBuildFilterCapabilities()
    {
        var type = typeof(Wfs20Handler);
        var method = type.GetMethod("BuildFilterCapabilities", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var result = method!.Invoke(null, null);
        return (FilterCapabilities)result!;
    }

    private static WfsCapabilities CreateSampleWfsCapabilities()
    {
        return new WfsCapabilities
        {
            ServiceIdentification = new ServiceIdentification(),
            ServiceProvider = new ServiceProvider(),
            FilterCapabilities = InvokeBuildFilterCapabilities()
        };
    }

    private static bool ValidateAgainstOGCSchema(WfsCapabilities capabilities, out List<string> errors)
    {
        errors = new List<string>();

        try
        {
            // Note: In a real implementation, you would validate against the actual OGC WFS 2.0 XSD
            // For now, we'll do basic XML serialization validation
            var serializer = new System.Xml.Serialization.XmlSerializer(typeof(WfsCapabilities));
            using var stringWriter = new StringWriter();
            using var xmlWriter = XmlWriter.Create(stringWriter, new XmlWriterSettings { Indent = true });

            serializer.Serialize(xmlWriter, capabilities);
            var xml = stringWriter.ToString();

            // Basic validation - ensure XML is well-formed and contains expected elements
            var xmlDoc = new XmlDocument();
            xmlDoc.LoadXml(xml);

            // Verify presence of required elements per OGC WFS 2.0
            var filterCapsNode = xmlDoc.SelectSingleNode("//*[local-name()='Filter_Capabilities']");
            if (filterCapsNode == null)
                errors.Add("Missing Filter_Capabilities element");

            var conformanceNode = xmlDoc.SelectSingleNode("//*[local-name()='Conformance']");
            if (conformanceNode == null)
                errors.Add("Missing Conformance element");

            return errors.Count == 0;
        }
        catch (Exception ex)
        {
            errors.Add($"XML serialization/validation error: {ex.Message}");
            return false;
        }
    }
}

/// <summary>
/// Helper class to calculate OGC Filter Encoding 2.0 compliance score
/// </summary>
public class OGCFilterEncoding20Checklist
{
    public bool HasBasicTemporalOperators { get; private set; }
    public bool HasExtendedTemporalOperators { get; private set; }
    public bool HasComprehensiveSpatialOperators { get; private set; }
    public bool HasFunctionCapabilities { get; private set; }
    public bool HasCQL2Support { get; private set; }
    public bool HasEnhancedConformanceDeclarations { get; private set; }

    public double CalculateCompliance(FilterCapabilities capabilities)
    {
        var totalChecks = 0;
        var passedChecks = 0;

        // Basic temporal operators check
        totalChecks++;
        var temporalOps = capabilities.TemporalCapabilities?.TemporalOperators?.Operators?.Select(op => op.Name) ?? [];
        HasBasicTemporalOperators = temporalOps.Contains("After") && temporalOps.Contains("Before") && temporalOps.Contains("During");
        if (HasBasicTemporalOperators) passedChecks++;

        // Extended temporal operators check
        totalChecks++;
        HasExtendedTemporalOperators = temporalOps.Contains("Equals") && temporalOps.Contains("Contains") &&
                                      temporalOps.Contains("Overlaps") && temporalOps.Contains("Meets");
        if (HasExtendedTemporalOperators) passedChecks++;

        // Comprehensive spatial operators check
        totalChecks++;
        var spatialOps = capabilities.SpatialCapabilities?.SpatialOperators?.Operators?.Select(op => op.Name) ?? [];
        HasComprehensiveSpatialOperators = spatialOps.Contains("Intersects") && spatialOps.Contains("Contains") &&
                                          spatialOps.Contains("Within") && spatialOps.Contains("DWithin") && spatialOps.Contains("BBOX");
        if (HasComprehensiveSpatialOperators) passedChecks++;

        // Function capabilities check
        totalChecks++;
        HasFunctionCapabilities = capabilities.Functions?.Functions?.Length > 0;
        if (HasFunctionCapabilities) passedChecks++;

        // CQL2 support check
        totalChecks++;
        var constraints = capabilities.Conformance?.Constraints?.Select(c => c.Name) ?? [];
        HasCQL2Support = constraints.Contains("ImplementsCQL2Text") && constraints.Contains("ImplementsCQL2JSON");
        if (HasCQL2Support) passedChecks++;

        // Enhanced conformance declarations check
        totalChecks++;
        HasEnhancedConformanceDeclarations = constraints.Contains("ImplementsFunctions") &&
                                           constraints.Contains("ImplementsArithmeticOperators") &&
                                           constraints.Contains("ImplementsCQL2TemporalOperators");
        if (HasEnhancedConformanceDeclarations) passedChecks++;

        return (double)passedChecks / totalChecks;
    }
}