// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.Ogc.Classic.Wfs20.Models;
using System.Xml;
using System.Xml.Serialization;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

/// <summary>
/// Tests for WFS 2.0 filter capabilities advertised by the runtime.
/// </summary>
public class Wfs20EnhancedFilterCapabilitiesTests
{
    [Fact]
    public void BuildFilterCapabilities_ShouldAdvertiseMinimumTemporalConformance()
    {
        // Act
        var filterCapabilities = Wfs20FilterCapabilitiesFactory.Build();

        // Assert
        filterCapabilities.Should().NotBeNull();
        filterCapabilities.TemporalCapabilities.Should().NotBeNull();
        filterCapabilities.TemporalCapabilities!.TemporalOperators!.Operators
            .Select(op => op.Name)
            .Should().BeEquivalentTo("After", "Before", "During");
        filterCapabilities.TemporalCapabilities.TemporalOperands!.Operands
            .Select(op => op.Name.Name)
            .Should().BeEquivalentTo("TimeInstant", "TimePeriod");

        var constraints = filterCapabilities.Conformance.Constraints
            .ToDictionary(c => c.Name, c => c.DefaultValue);
        constraints["ImplementsMinTemporalFilter"].Should().Be("TRUE");
        constraints["ImplementsTemporalFilter"].Should().Be("FALSE");
        constraints["ImplementsTemporalInstant"].Should().Be("TRUE");
        constraints["ImplementsTemporalPeriod"].Should().Be("TRUE");
    }

    [Fact]
    public void BuildFilterCapabilities_ShouldAdvertiseSupportedComparisonOperatorsOnly()
    {
        // Act
        var filterCapabilities = Wfs20FilterCapabilitiesFactory.Build();

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
        var filterCapabilities = Wfs20FilterCapabilitiesFactory.Build();

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
        var filterCapabilities = Wfs20FilterCapabilitiesFactory.Build();

        // Assert
        filterCapabilities.Should().NotBeNull();
        filterCapabilities.Functions.Should().BeNull("FES 2.0 Functions cannot be serialized as an empty element");
    }

    [Fact]
    public void BuildFilterCapabilities_ShouldIncludeEnhancedConformanceConstraints()
    {
        // Act
        var filterCapabilities = Wfs20FilterCapabilitiesFactory.Build();

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
        constraints.Should().Contain(c => c.Name == "ImplementsMinTemporalFilter" && c.DefaultValue == "TRUE");
        constraints.Should().Contain(c => c.Name == "ImplementsTemporalFilter" && c.DefaultValue == "FALSE");
        constraints.Should().Contain(c => c.Name == "ImplementsTemporalInstant" && c.DefaultValue == "TRUE");
        constraints.Should().Contain(c => c.Name == "ImplementsTemporalPeriod" && c.DefaultValue == "TRUE");
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
        var filterCapabilities = Wfs20FilterCapabilitiesFactory.Build();

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
        xml.Should().MatchRegex(@"<([A-Za-z_][\w.-]*:)?TemporalOperators(\s|>|/)");
        xml.Should().Contain("Temporal_Capabilities");
        xml.Should().Contain("SpatialOperators");
        xml.Should().Contain("ComparisonOperators");
        xml.Should().NotContain("<Functions");
        xml.Should().NotContain("ST_NumGeometries");
    }
}
