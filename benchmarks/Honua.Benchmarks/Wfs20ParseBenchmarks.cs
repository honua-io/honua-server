// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using BenchmarkDotNet.Attributes;
using Honua.Core.Queries.Filters;
using Honua.Core.Queries.Filters.Fes20;

namespace Honua.Benchmarks;

/// <summary>
/// FES 2.0 filter parsing is invoked on every WFS GetFeature/Transaction request
/// that ships a <c>FILTER</c> parameter. The parser is pure CPU (XML parse +
/// AST projection) so it belongs in the microbenchmark harness rather than the
/// load rig. We exercise a representative spread of filter shapes — a trivial
/// equality, a deeply nested logical tree, and a spatial intersect with a GML
/// linestring — so regressions in any common WFS payload surface here.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory(Categories.Ogc, Categories.Filter)]
public class Wfs20ParseBenchmarks
{
    private string _simpleEqualsFilter = null!;
    private string _nestedLogicalFilter = null!;
    private string _spatialIntersectsFilter = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Trivial single-predicate filter — the modal shape for attribute
        // queries from web clients; dominated by XML reader setup cost.
        _simpleEqualsFilter = """
            <fes:Filter xmlns:fes="http://www.opengis.net/fes/2.0">
              <fes:PropertyIsEqualTo>
                <fes:ValueReference>name</fes:ValueReference>
                <fes:Literal>Honua</fes:Literal>
              </fes:PropertyIsEqualTo>
            </fes:Filter>
            """;

        // Deeply nested AND/OR tree — exercises ParseExpression recursion plus
        // the FilterParserGuard depth check on a representative complex query.
        var nested = new StringBuilder();
        nested.Append("""<fes:Filter xmlns:fes="http://www.opengis.net/fes/2.0">""");
        nested.Append("<fes:And>");
        for (var i = 0; i < 8; i++)
        {
            nested.Append("<fes:Or>");
            nested.Append("<fes:PropertyIsEqualTo>");
            nested.Append("<fes:ValueReference>field");
            nested.Append(i.ToString(CultureInfo.InvariantCulture));
            nested.Append("</fes:ValueReference>");
            nested.Append("<fes:Literal>value");
            nested.Append(i.ToString(CultureInfo.InvariantCulture));
            nested.Append("</fes:Literal>");
            nested.Append("</fes:PropertyIsEqualTo>");
            nested.Append("<fes:PropertyIsGreaterThan>");
            nested.Append("<fes:ValueReference>score</fes:ValueReference>");
            nested.Append("<fes:Literal>");
            nested.Append(i.ToString(CultureInfo.InvariantCulture));
            nested.Append("</fes:Literal>");
            nested.Append("</fes:PropertyIsGreaterThan>");
            nested.Append("</fes:Or>");
        }

        nested.Append("</fes:And>");
        nested.Append("</fes:Filter>");
        _nestedLogicalFilter = nested.ToString();

        // Spatial intersect with a small GML LineString — exercises the GML
        // geometry path, which is the WFS hot spot for map-tool queries.
        _spatialIntersectsFilter = """
            <fes:Filter xmlns:fes="http://www.opengis.net/fes/2.0" xmlns:gml="http://www.opengis.net/gml/3.2">
              <fes:Intersects>
                <fes:ValueReference>geom</fes:ValueReference>
                <gml:LineString srsName="EPSG:4326">
                  <gml:posList>-122.5 37.7 -122.4 37.8 -122.3 37.9</gml:posList>
                </gml:LineString>
              </fes:Intersects>
            </fes:Filter>
            """;
    }

    [Benchmark(Baseline = true, Description = "ParseFilter simple PropertyIsEqualTo")]
    public FilterExpression ParseSimpleEquals()
        => Fes20Parser.ParseFilter(_simpleEqualsFilter);

    [Benchmark(Description = "ParseFilter nested And/Or tree (8x)")]
    public FilterExpression ParseNestedLogical()
        => Fes20Parser.ParseFilter(_nestedLogicalFilter);

    [Benchmark(Description = "ParseFilter spatial Intersects LineString")]
    public FilterExpression ParseSpatialIntersects()
        => Fes20Parser.ParseFilter(_spatialIntersectsFilter);
}
