// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Protocols.OData.Models;
using Honua.Protocols.OData.Services;
using Xunit;

namespace Honua.Protocols.OData.Tests.Services;

/// <summary>
/// Unit tests for OData <c>$apply</c> pipeline parsing (#1636). The handler previously parsed only
/// a single transform, so on a <c>filter(...)/groupby(...)</c> pipeline the filter() regex greedily
/// swallowed the rest of the pipeline and produced a broken filter — 400ing extent-scoped
/// aggregation such as <c>filter(geo.intersects(...))/groupby(...)</c>.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Component", "OData")]
[Trait("Feature", "Apply")]
public sealed class ODataApplyPipelineTests
{
    [Fact]
    public void SplitApplyTransforms_SplitsPipelineOnTopLevelSlash()
    {
        var segments = ODataAggregationHandler.SplitApplyTransforms(
            "filter(population gt 1000)/groupby((state), aggregate(population with sum as Total))");

        Assert.Equal(2, segments.Count);
        Assert.Equal("filter(population gt 1000)", segments[0]);
        Assert.Equal("groupby((state), aggregate(population with sum as Total))", segments[1]);
    }

    [Fact]
    public void SplitApplyTransforms_PreservesSlashesInsideParensAndQuotes()
    {
        // The geography literal and nested parens must not be split, even though a real WKT/CRS
        // value could contain characters the splitter scans past.
        const string apply =
            "filter(geo.intersects(Geometry, geography'SRID=4326;POLYGON((0 0,0 1,1 1,1 0,0 0))'))/groupby((gisacres))";

        var segments = ODataAggregationHandler.SplitApplyTransforms(apply);

        Assert.Equal(2, segments.Count);
        Assert.StartsWith("filter(geo.intersects(", segments[0], System.StringComparison.Ordinal);
        Assert.EndsWith("))", segments[0], System.StringComparison.Ordinal);
        Assert.Equal("groupby((gisacres))", segments[1]);
    }

    [Fact]
    public void SplitApplyTransforms_SingleTransform_ReturnsOneSegment()
    {
        var segments = ODataAggregationHandler.SplitApplyTransforms("aggregate($count as Total)");

        Assert.Single(segments);
        Assert.Equal("aggregate($count as Total)", segments[0]);
    }

    [Fact]
    public void ParseApplyExpression_FilterSegmentOfPipeline_ParsesFilterOnly()
    {
        // Each segment parses independently — the leading filter must capture only its own
        // expression, not the trailing groupby it used to greedily absorb.
        var segments = ODataAggregationHandler.SplitApplyTransforms(
            "filter(gisacres gt 5)/groupby((gisacres))");

        var filter = ODataAggregationHandler.ParseApplyExpression(segments[0]);
        var groupby = ODataAggregationHandler.ParseApplyExpression(segments[1]);

        Assert.Equal(AggregationType.Filter, filter.Type);
        Assert.Equal("gisacres gt 5", filter.FilterExpression);
        Assert.Equal(AggregationType.GroupBy, groupby.Type);
    }
}
