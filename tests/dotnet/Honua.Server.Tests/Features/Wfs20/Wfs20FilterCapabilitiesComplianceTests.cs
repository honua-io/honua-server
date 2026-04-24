// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Wfs20.Models;
using Honua.Server.Features.Wfs20.Services;
using System.Reflection;

namespace Honua.Server.Tests.Features.Wfs20;

/// <summary>
/// Tests that WFS 2.0 FilterCapabilities advertise the capabilities implemented by the runtime.
/// </summary>
public class Wfs20FilterCapabilitiesComplianceTests
{
    [Fact]
    public void FilterCapabilities_TemporalOperators_ShouldAdvertiseRuntimeSupportedOperators()
    {
        var filterCapabilities = GetActualFilterCapabilities();
        var operatorNames = filterCapabilities.TemporalCapabilities!.TemporalOperators!.Operators
            .Select(op => op.Name)
            .ToArray();

        operatorNames.Should().BeEquivalentTo(
            "After",
            "Before",
            "During",
            "Contains",
            "Equals",
            "Disjoint",
            "Intersects",
            "Meets",
            "MetBy",
            "Overlaps",
            "OverlappedBy",
            "Starts",
            "StartedBy",
            "Finishes",
            "FinishedBy");
    }

    [Fact]
    public void FilterCapabilities_FunctionsAndCql2_ShouldAdvertiseRuntimeCapabilities()
    {
        var filterCapabilities = GetActualFilterCapabilities();
        var constraintDict = filterCapabilities.Conformance.Constraints
            .ToDictionary(c => c.Name, c => c.DefaultValue);

        filterCapabilities.Functions.Should().NotBeNull();
        filterCapabilities.Functions!.Functions.Select(function => function.Name)
            .Should().Contain(["ST_NumGeometries", "UPPER", "SQRT", "NOW", "COUNT"]);
        filterCapabilities.Functions.Functions.Should().HaveCountGreaterOrEqualTo(35);
        constraintDict["ImplementsFunctions"].Should().Be("TRUE");
        constraintDict["ImplementsArithmeticOperators"].Should().Be("TRUE");
        constraintDict["ImplementsExtendedOperators"].Should().Be("TRUE");
        constraintDict["ImplementsCQL2Text"].Should().Be("TRUE");
        constraintDict["ImplementsCQL2JSON"].Should().Be("TRUE");
        constraintDict["ImplementsCQL2BasicCQL"].Should().Be("TRUE");
        constraintDict["ImplementsCQL2AdvancedComparison"].Should().Be("TRUE");
        constraintDict["ImplementsCQL2BasicSpatial"].Should().Be("TRUE");
        constraintDict["ImplementsCQL2SpatialOperators"].Should().Be("TRUE");
        constraintDict["ImplementsCQL2TemporalOperators"].Should().Be("TRUE");
        constraintDict["ImplementsCQL2ArrayOperators"].Should().Be("FALSE");
        constraintDict["ImplementsCQL2Functions"].Should().Be("TRUE");
    }

    [Fact]
    public void FilterCapabilities_SpatialAndComparisonOperators_ShouldMatchRuntimeSupport()
    {
        var filterCapabilities = GetActualFilterCapabilities();

        var comparisonOperators = filterCapabilities.ScalarCapabilities!.ComparisonOperators!.Operators
            .Select(op => op.Name)
            .ToArray();
        foreach (var expected in new[]
                 {
                     "PropertyIsEqualTo",
                     "PropertyIsNotEqualTo",
                     "PropertyIsLessThan",
                     "PropertyIsGreaterThan",
                     "PropertyIsLessThanOrEqualTo",
                     "PropertyIsGreaterThanOrEqualTo",
                     "PropertyIsLike",
                     "PropertyIsNil",
                     "PropertyIsNull",
                     "PropertyIsBetween"
                 })
        {
            comparisonOperators.Should().Contain(expected);
        }
        comparisonOperators.Should().NotContain("PropertyIsIn");
        comparisonOperators.Should().NotContain("PropertyIsNotIn");

        var geometryOperands = filterCapabilities.SpatialCapabilities!.GeometryOperands!.Operands
            .Select(op => op.Name.Name)
            .ToArray();
        geometryOperands.Should().BeEquivalentTo("Envelope", "Point", "LineString", "Curve", "Polygon", "Surface");

        var spatialOperators = filterCapabilities.SpatialCapabilities.SpatialOperators!.Operators
            .Select(op => op.Name)
            .ToArray();
        foreach (var expected in new[]
                 {
                     "BBOX",
                     "Intersects",
                     "Contains",
                     "Within",
                     "Crosses",
                     "Touches",
                     "Overlaps",
                     "Disjoint",
                     "Equals",
                     "EnvelopeIntersects",
                     "DWithin",
                     "Beyond"
                 })
        {
            spatialOperators.Should().Contain(expected);
        }
        spatialOperators.Should().NotContain("Relate");
    }

    private static FilterCapabilities GetActualFilterCapabilities()
    {
        var type = typeof(Wfs20Handler);
        var method = type.GetMethod("BuildFilterCapabilities",
            BindingFlags.NonPublic | BindingFlags.Static);
        return (FilterCapabilities)method!.Invoke(null, null)!;
    }
}
