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
/// Tests for enhanced WFS 2.0 filter capabilities for 95% OGC compliance
/// </summary>
public class Wfs20EnhancedFilterCapabilitiesTests
{
    [Fact]
    public void BuildFilterCapabilities_ShouldIncludeComprehensiveTemporalOperators()
    {
        // Act
        var filterCapabilities = InvokeBuildFilterCapabilities();

        // Assert
        filterCapabilities.Should().NotBeNull();
        filterCapabilities.TemporalCapabilities.Should().NotBeNull();
        filterCapabilities.TemporalCapabilities!.TemporalOperators.Should().NotBeNull();

        var temporalOperators = filterCapabilities.TemporalCapabilities.TemporalOperators!.Operators;
        temporalOperators.Should().NotBeEmpty();

        // Verify all OGC Filter Encoding 2.0 temporal operators are present
        var operatorNames = temporalOperators.Select(op => op.Name).ToList();

        // Basic temporal operators
        operatorNames.Should().Contain("After");
        operatorNames.Should().Contain("Before");
        operatorNames.Should().Contain("During");
        operatorNames.Should().Contain("Equals");

        // Allen's interval relations
        operatorNames.Should().Contain("Contains");
        operatorNames.Should().Contain("Overlaps");
        operatorNames.Should().Contain("Meets");
        operatorNames.Should().Contain("OverlappedBy");
        operatorNames.Should().Contain("MetBy");
        operatorNames.Should().Contain("Starts");
        operatorNames.Should().Contain("StartedBy");
        operatorNames.Should().Contain("Finishes");
        operatorNames.Should().Contain("FinishedBy");

        // Additional temporal predicates
        operatorNames.Should().Contain("Intersects");
        operatorNames.Should().Contain("Disjoint");

        // Should have significantly more operators than the original 3
        temporalOperators.Length.Should().BeGreaterThan(10);
    }

    [Fact]
    public void BuildFilterCapabilities_ShouldIncludeEnhancedComparisonOperators()
    {
        // Act
        var filterCapabilities = InvokeBuildFilterCapabilities();

        // Assert
        filterCapabilities.Should().NotBeNull();
        filterCapabilities.ScalarCapabilities.Should().NotBeNull();
        filterCapabilities.ScalarCapabilities!.ComparisonOperators.Should().NotBeNull();

        var comparisonOperators = filterCapabilities.ScalarCapabilities.ComparisonOperators!.Operators;
        comparisonOperators.Should().NotBeEmpty();

        var operatorNames = comparisonOperators.Select(op => op.Name).ToList();

        // Basic comparison operators
        operatorNames.Should().Contain("PropertyIsEqualTo");
        operatorNames.Should().Contain("PropertyIsNotEqualTo");
        operatorNames.Should().Contain("PropertyIsLessThan");
        operatorNames.Should().Contain("PropertyIsGreaterThan");
        operatorNames.Should().Contain("PropertyIsLessThanOrEqualTo");
        operatorNames.Should().Contain("PropertyIsGreaterThanOrEqualTo");

        // Pattern matching operators
        operatorNames.Should().Contain("PropertyIsLike");

        // Null checks
        operatorNames.Should().Contain("PropertyIsNil");
        operatorNames.Should().Contain("PropertyIsNull");

        // Range and set operators
        operatorNames.Should().Contain("PropertyIsBetween");
        operatorNames.Should().Contain("PropertyIsIn");
        operatorNames.Should().Contain("PropertyIsNotIn");

        // Should have more operators than the original basic set
        comparisonOperators.Length.Should().BeGreaterThan(10);
    }

    [Fact]
    public void BuildFilterCapabilities_ShouldIncludeEnhancedSpatialOperators()
    {
        // Act
        var filterCapabilities = InvokeBuildFilterCapabilities();

        // Assert
        filterCapabilities.Should().NotBeNull();
        filterCapabilities.SpatialCapabilities.Should().NotBeNull();
        filterCapabilities.SpatialCapabilities!.SpatialOperators.Should().NotBeNull();
        filterCapabilities.SpatialCapabilities!.GeometryOperands.Should().NotBeNull();

        var spatialOperators = filterCapabilities.SpatialCapabilities.SpatialOperators!.Operators;
        var geometryOperands = filterCapabilities.SpatialCapabilities.GeometryOperands!.Operands;

        spatialOperators.Should().NotBeEmpty();
        geometryOperands.Should().NotBeEmpty();

        // Verify comprehensive spatial operators
        var spatialOperatorNames = spatialOperators.Select(op => op.Name).ToList();
        spatialOperatorNames.Should().Contain("BBOX");
        spatialOperatorNames.Should().Contain("Intersects");
        spatialOperatorNames.Should().Contain("Contains");
        spatialOperatorNames.Should().Contain("Within");
        spatialOperatorNames.Should().Contain("Crosses");
        spatialOperatorNames.Should().Contain("Touches");
        spatialOperatorNames.Should().Contain("Overlaps");
        spatialOperatorNames.Should().Contain("Disjoint");
        spatialOperatorNames.Should().Contain("Equals");
        spatialOperatorNames.Should().Contain("DWithin");
        spatialOperatorNames.Should().Contain("Beyond");
        spatialOperatorNames.Should().Contain("Relate");

        // Verify comprehensive geometry operands including multi-geometries
        var geometryNames = geometryOperands.Select(op => op.Name.Name).ToList();
        geometryNames.Should().Contain("Point");
        geometryNames.Should().Contain("LineString");
        geometryNames.Should().Contain("Polygon");
        geometryNames.Should().Contain("MultiPoint");
        geometryNames.Should().Contain("MultiLineString");
        geometryNames.Should().Contain("MultiPolygon");
        geometryNames.Should().Contain("GeometryCollection");
    }

    [Fact]
    public void BuildFilterCapabilities_ShouldIncludeComprehensiveFunctionsList()
    {
        // Act
        var filterCapabilities = InvokeBuildFilterCapabilities();

        // Assert
        filterCapabilities.Should().NotBeNull();
        filterCapabilities.Functions.Should().NotBeNull();

        var functions = filterCapabilities.Functions!.Functions;
        functions.Should().NotBeEmpty();

        var functionNames = functions.Select(f => f.Name).ToList();

        // String functions
        functionNames.Should().Contain("UPPER");
        functionNames.Should().Contain("LOWER");
        functionNames.Should().Contain("CONCAT");
        functionNames.Should().Contain("SUBSTRING");
        functionNames.Should().Contain("LENGTH");

        // Math functions
        functionNames.Should().Contain("ABS");
        functionNames.Should().Contain("CEIL");
        functionNames.Should().Contain("FLOOR");
        functionNames.Should().Contain("ROUND");
        functionNames.Should().Contain("SQRT");
        functionNames.Should().Contain("SIN");
        functionNames.Should().Contain("COS");
        functionNames.Should().Contain("POWER");
        functionNames.Should().Contain("MOD");

        // Spatial functions
        functionNames.Should().Contain("ST_Area");
        functionNames.Should().Contain("ST_Length");
        functionNames.Should().Contain("ST_Distance");
        functionNames.Should().Contain("ST_Buffer");
        functionNames.Should().Contain("ST_Centroid");
        functionNames.Should().Contain("ST_IsValid");

        // Date/time functions
        functionNames.Should().Contain("YEAR");
        functionNames.Should().Contain("MONTH");
        functionNames.Should().Contain("DAY");
        functionNames.Should().Contain("NOW");

        // Aggregate functions
        functionNames.Should().Contain("COUNT");
        functionNames.Should().Contain("SUM");
        functionNames.Should().Contain("AVG");
        functionNames.Should().Contain("MIN");
        functionNames.Should().Contain("MAX");

        // Should include substantial number of functions for comprehensive compliance
        functions.Length.Should().BeGreaterThan(30);
    }

    [Fact]
    public void BuildFilterCapabilities_ShouldIncludeEnhancedConformanceConstraints()
    {
        // Act
        var filterCapabilities = InvokeBuildFilterCapabilities();

        // Assert
        filterCapabilities.Should().NotBeNull();
        filterCapabilities.Conformance.Should().NotBeNull();

        var constraints = filterCapabilities.Conformance.Constraints;
        constraints.Should().NotBeEmpty();

        var constraintNames = constraints.Select(c => c.Name).ToList();

        // Core OGC Filter Encoding 2.0 conformance classes
        constraintNames.Should().Contain("ImplementsQuery");
        constraintNames.Should().Contain("ImplementsAdHocQuery");
        constraintNames.Should().Contain("ImplementsResourceId");
        constraintNames.Should().Contain("ImplementsStandardFilter");
        constraintNames.Should().Contain("ImplementsSpatialFilter");
        constraintNames.Should().Contain("ImplementsTemporalFilter");
        constraintNames.Should().Contain("ImplementsFunctions");

        // Enhanced capabilities
        constraintNames.Should().Contain("ImplementsBBOX");
        constraintNames.Should().Contain("ImplementsDistanceBuffer");
        constraintNames.Should().Contain("ImplementsTemporalInstant");
        constraintNames.Should().Contain("ImplementsTemporalPeriod");
        constraintNames.Should().Contain("ImplementsArithmeticOperators");
        constraintNames.Should().Contain("ImplementsLogicalOperators");
        constraintNames.Should().Contain("ImplementsComparisonOperators");

        // CQL2 support
        constraintNames.Should().Contain("ImplementsCQL2Text");
        constraintNames.Should().Contain("ImplementsCQL2JSON");
        constraintNames.Should().Contain("ImplementsCQL2BasicCQL");
        constraintNames.Should().Contain("ImplementsCQL2SpatialOperators");
        constraintNames.Should().Contain("ImplementsCQL2TemporalOperators");
        constraintNames.Should().Contain("ImplementsCQL2ArrayOperators");
        constraintNames.Should().Contain("ImplementsCQL2Functions");

        // Should have significantly more conformance declarations than the original ~13
        constraints.Length.Should().BeGreaterThan(20);
    }

    [Fact]
    public void FilterCapabilities_ShouldSerializeToValidXml()
    {
        // Act
        var filterCapabilities = InvokeBuildFilterCapabilities();

        // Assert - Should serialize without errors
        var serializer = new XmlSerializer(typeof(FilterCapabilities));
        using var stringWriter = new StringWriter();
        using var xmlWriter = XmlWriter.Create(stringWriter, new XmlWriterSettings { Indent = true });

        var action = () => serializer.Serialize(xmlWriter, filterCapabilities);
        action.Should().NotThrow();

        var xml = stringWriter.ToString();
        xml.Should().NotBeEmpty();
        xml.Should().Contain("Filter_Capabilities");
        xml.Should().Contain("Conformance");
        xml.Should().Contain("TemporalOperators");
        xml.Should().Contain("SpatialOperators");
        xml.Should().Contain("ComparisonOperators");
        xml.Should().Contain("Functions");
    }

    /// <summary>
    /// Uses reflection to invoke the private BuildFilterCapabilities method
    /// </summary>
    private static FilterCapabilities InvokeBuildFilterCapabilities()
    {
        var type = typeof(Wfs20Handler);
        var method = type.GetMethod("BuildFilterCapabilities", BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull("BuildFilterCapabilities method should exist");

        var result = method!.Invoke(null, null);
        return (FilterCapabilities)result!;
    }
}