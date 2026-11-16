// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Authorization.Services;
using Honua.Core.Queries.Filters;
using Honua.TestKit.Attributes;
using Moq;

namespace Honua.Core.Tests.Features.Authorization;

public sealed class RlsPolicyRestrictionsTests
{
    private const string Polygon = """{"type":"Polygon","coordinates":[[[0,0],[2,0],[2,2],[0,0]]]}""";
    private readonly FilterExpressionService _filters = new(Mock.Of<IFilterExpressionTranslator>());

    [UnitTest]
    public void Parse_UnconfiguredPolicy_PreservesLegacyBehavior()
        => RlsPolicyRestrictions.Parse(_filters, null, null, null).Should().BeNull();

    [UnitTest]
    public void Parse_ConfiguredFilter_UsesSharedExpression()
        => RlsPolicyRestrictions.Parse(_filters, "name = 'allowed'", null, null)
            .Should().BeOfType<BinaryExpression>();

    [UnitTest]
    public void Parse_FilterAndArea_CombinesWithIntersection()
    {
        var result = RlsPolicyRestrictions.Parse(_filters, "name = 'allowed'", Polygon, "shape");

        var combined = result.Should().BeOfType<BinaryExpression>().Subject;
        combined.Operator.Should().Be(BinaryOperator.And);
        combined.Right.Should().BeOfType<SpatialPredicate>();
    }

    [UnitTheory]
    [InlineData(Polygon)]
    [InlineData("""{"type":"MultiPolygon","coordinates":[[[[0,0],[2,0],[2,2],[0,0]]]]}""")]
    public void Parse_ConfiguredArea_UsesSpatialPredicate(string area)
        => RlsPolicyRestrictions.Parse(_filters, null, area, "shape")
            .Should().BeOfType<SpatialPredicate>();

    [UnitTheory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("name =")]
    public void Parse_InvalidFilter_RejectsConfiguration(string filter)
    {
        var action = () => RlsPolicyRestrictions.Parse(_filters, filter, null, null);
        action.Should().Throw<ArgumentException>();
    }

    [UnitTheory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("invalid")]
    [InlineData("""{"type":"Point","coordinates":[0,0]}""")]
    [InlineData("""{"type":"Polygon","coordinates":[]}""")]
    [InlineData("""{"type":"Polygon","coordinates":[[]]}""")]
    [InlineData("""{"type":"Polygon","coordinates":[[[181,0],[2,0],[2,2],[181,0]]]}""")]
    [InlineData("""{"type":"Polygon","coordinates":[[[0,91],[2,0],[2,2],[0,91]]]}""")]
    [InlineData("""{"type":"Polygon","coordinates":[[["0",0],[2,0],[2,2],["0",0]]]}""")]
    [InlineData("""{"type":"Polygon","crs":{},"coordinates":[[[0,0],[2,0],[2,2],[0,0]]]}""")]
    public void Parse_InvalidArea_RejectsConfiguration(string area)
    {
        var action = () => RlsPolicyRestrictions.Parse(_filters, null, area, "shape");
        action.Should().Throw<ArgumentException>();
    }

    [UnitTest]
    public void Parse_AreaWithoutGeometryField_RejectsConfiguration()
    {
        var action = () => RlsPolicyRestrictions.Parse(_filters, null, Polygon, null);
        action.Should().Throw<ArgumentException>();
    }

    [UnitTest]
    public void Parse_RestrictionsBeyondLimits_RejectsConfiguration()
    {
        var filter = () => RlsPolicyRestrictions.Parse(_filters, new string('a', 4097), null, null);
        var area = () => RlsPolicyRestrictions.Parse(_filters, null, new string(' ', 65537), "shape");
        filter.Should().Throw<ArgumentException>();
        area.Should().Throw<ArgumentException>();
    }
}
