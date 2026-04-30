// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Protocols.Ogc.Classic.Wfs20.Models;
using Honua.Server.Features.Protocols.Ogc.Classic.Wfs20.Services;
using System.Reflection;
using System.Xml;
using System.Xml.Serialization;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

/// <summary>
/// Tests for WFS 2.0 filter capabilities advertised by the runtime.
/// </summary>
public class Wfs20EnhancedFilterCapabilitiesTests
{
    [Fact]
    public void BuildFilterCapabilities_ShouldAdvertiseSupportedTemporalOperatorsOnly()
    {
        // Act
        var filterCapabilities = InvokeBuildFilterCapabilities();

        // Assert
        filterCapabilities.Should().NotBeNull();
        filterCapabilities.TemporalCapabilities.Should().NotBeNull();
        filterCapabilities.TemporalCapabilities!.TemporalOperators.Should().NotBeNull();

        var temporalOperators = filterCapabilities.TemporalCapabilities.TemporalOperators!.Operators;
        temporalOperators.Should().NotBeEmpty();

        var operatorNames = temporalOperators.Select(op => op.Name).ToList();

        operatorNames.Should().BeEquivalentTo(
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
            "AnyInteracts");
    }

    [Fact]
    public void BuildFilterCapabilities_ShouldAdvertiseSupportedComparisonOperatorsOnly()
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

        // Range operators
        operatorNames.Should().Contain("PropertyIsBetween");
        operatorNames.Should().NotContain("PropertyIsIn");
        operatorNames.Should().NotContain("PropertyIsNotIn");

        comparisonOperators.Length.Should().Be(10);
    }

    [Fact]
    public void BuildFilterCapabilities_ShouldAdvertiseSupportedSpatialOperatorsOnly()
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
        spatialOperatorNames.Should().NotContain("EnvelopeIntersects");
        spatialOperatorNames.Should().NotContain("Relate");

        var geometryNames = geometryOperands.Select(op => op.Name.Name).ToList();
        geometryNames.Should().BeEquivalentTo("Envelope", "Point", "LineString", "Curve", "Polygon", "Surface");
    }

    [Fact]
    public void BuildFilterCapabilities_ShouldNotAdvertiseUnsupportedFunctions()
    {
        // Act
        var filterCapabilities = InvokeBuildFilterCapabilities();

        // Assert
        filterCapabilities.Should().NotBeNull();
        filterCapabilities.Functions.Should().NotBeNull();
        filterCapabilities.Functions!.Functions.Should().BeEmpty();
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

        constraints.Should().Contain(c => c.Name == "ImplementsFunctions" && c.DefaultValue == "FALSE");
        constraints.Should().Contain(c => c.Name == "ImplementsArithmeticOperators" && c.DefaultValue == "FALSE");
        constraints.Should().Contain(c => c.Name == "ImplementsExtendedOperators" && c.DefaultValue == "TRUE");
        constraints.Should().Contain(c => c.Name == "ImplementsCQL2Text" && c.DefaultValue == "FALSE");
        constraints.Should().Contain(c => c.Name == "ImplementsCQL2JSON" && c.DefaultValue == "FALSE");
        constraints.Should().Contain(c => c.Name == "ImplementsCQL2BasicCQL" && c.DefaultValue == "FALSE");
        constraints.Should().Contain(c => c.Name == "ImplementsCQL2SpatialOperators" && c.DefaultValue == "FALSE");
        constraints.Should().Contain(c => c.Name == "ImplementsCQL2TemporalOperators" && c.DefaultValue == "FALSE");
        constraints.Should().Contain(c => c.Name == "ImplementsCQL2ArrayOperators" && c.DefaultValue == "FALSE");
        constraints.Should().Contain(c => c.Name == "ImplementsCQL2Functions" && c.DefaultValue == "FALSE");
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
        xml.Should().Contain("<Functions");
        xml.Should().NotContain("ST_NumGeometries");
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
