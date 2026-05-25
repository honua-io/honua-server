// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.GeoETL.Domain;
using Honua.Core.Features.GeoETL.Services.Connectors;
using Honua.TestKit.Attributes;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;

namespace Honua.Core.Tests.Features.GeoETL;

/// <summary>
/// Unit coverage for the managed CSV source connector. Exercises the lon/lat coordinate
/// and WKT geometry encodings wrapped from the existing <c>CsvFormatReader</c>, with no
/// native dependency.
/// </summary>
public sealed class CsvSourceConnectorTests
{
    [UnitTest]
    public async Task ReadAsync_LonLatColumns_ProducesPointFeatures()
    {
        const string csv = """
            name,lon,lat,score
            origin,0,0,10
            berlin,13.405,52.52,90
            """;
        var connector = new CsvSourceConnector();
        var config = new ConnectorConfig
        {
            Type = CsvSourceConnector.ConnectorType,
            Options = new Dictionary<string, string> { ["inline"] = csv }
        };

        var features = new List<IFeature>();
        await foreach (var feature in connector.ReadAsync(config))
        {
            features.Add(feature);
        }

        features.Should().HaveCount(2);
        var berlin = features.Single(f =>
            string.Equals(f.Attributes!.GetOptionalValue("name")?.ToString(), "berlin", StringComparison.Ordinal));
        var point = (Point)berlin.Geometry!;
        point.X.Should().BeApproximately(13.405, 0.0001);
        point.Y.Should().BeApproximately(52.52, 0.0001);
        berlin.Attributes!.GetOptionalValue("score").Should().Be("90");
    }

    [UnitTest]
    public async Task ReadAsync_WktColumn_ParsesGeometry()
    {
        const string csv = """
            id;wkt
            1;POINT (30 10)
            2;LINESTRING (0 0, 1 1)
            """;
        var connector = new CsvSourceConnector();
        var config = new ConnectorConfig
        {
            Type = CsvSourceConnector.ConnectorType,
            Options = new Dictionary<string, string> { ["inline"] = csv }
        };

        var features = new List<IFeature>();
        await foreach (var feature in connector.ReadAsync(config))
        {
            features.Add(feature);
        }

        features.Should().HaveCount(2);
        features[0].Geometry.Should().BeOfType<Point>();
        features[1].Geometry.Should().BeOfType<LineString>();
    }

    [UnitTest]
    public async Task ReadAsync_WithoutPathOrInline_Throws()
    {
        var connector = new CsvSourceConnector();
        var config = new ConnectorConfig { Type = CsvSourceConnector.ConnectorType };

        var act = async () =>
        {
            await foreach (var _ in connector.ReadAsync(config))
            {
            }
        };

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
