// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.GeoETL.Domain;
using Honua.Core.Features.GeoETL.Services.Connectors;
using Honua.TestKit.Attributes;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Core.Tests.Features.GeoETL;

/// <summary>
/// Unit coverage for the managed (GDAL-free) GeoETL file sinks: the GeoJSON file export
/// sink and the quarantine / dead-letter sink. Both write to a temp file and read it back
/// with the managed GeoJSON reader to assert the round-trip.
/// </summary>
public sealed class FileSinkConnectorTests
{
    private static readonly GeometryFactory Factory = new(new PrecisionModel(), 4326);

    [UnitTest]
    public async Task GeoJsonFileSink_WritesFeatureCollectionRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"honua-geoetl-sink-{Guid.NewGuid():N}.geojson");

        try
        {
            var sink = new GeoJsonFileSinkConnector();
            var config = new ConnectorConfig
            {
                Type = GeoJsonFileSinkConnector.ConnectorType,
                Options = new Dictionary<string, string> { ["path"] = path }
            };

            var result = await sink.WriteAsync(config, Features(
                new Feature(Factory.CreatePoint(new Coordinate(13.405, 52.52)),
                    new AttributesTable { { "name", "berlin" } }),
                new Feature(Factory.CreatePoint(new Coordinate(-122.4, 37.6)),
                    new AttributesTable { { "name", "sf" } })), "batch-1");

            result.FeaturesWritten.Should().Be(2);
            result.FeaturesRejected.Should().Be(0);

            var json = await File.ReadAllTextAsync(path);
            var collection = new GeoJsonReader().Read<FeatureCollection>(json);
            collection.Count.Should().Be(2);
            collection.Select(f => f.Attributes.GetOptionalValue("name")?.ToString())
                .Should().BeEquivalentTo(["berlin", "sf"]);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [UnitTest]
    public async Task QuarantineSink_TagsBatchIdAndReason_AndNeverThrows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"honua-geoetl-dlq-{Guid.NewGuid():N}.geojson");

        try
        {
            var sink = new QuarantineSinkConnector();
            var config = new ConnectorConfig
            {
                Type = QuarantineSinkConnector.ConnectorType,
                Options = new Dictionary<string, string> { ["path"] = path }
            };

            var withReason = new Feature(Factory.CreatePoint(new Coordinate(0, 0)),
                new AttributesTable { { "_quarantine_reason", "bad cast" } });
            var nullGeometry = new Feature(null, new AttributesTable { { "id", 7L } });

            var result = await sink.WriteAsync(config, Features(withReason, nullGeometry), "batch-dlq");

            // Quarantined rows are rejects, not durable writes.
            result.FeaturesWritten.Should().Be(0);
            result.FeaturesRejected.Should().Be(2);

            var json = await File.ReadAllTextAsync(path);
            var collection = new GeoJsonReader().Read<FeatureCollection>(json);
            collection.Count.Should().Be(2);
            collection.Should().AllSatisfy(f =>
                f.Attributes.GetOptionalValue("_batch_id").Should().Be("batch-dlq"));
            collection.Select(f => Convert.ToString(
                    f.Attributes.GetOptionalValue("_quarantine_reason"),
                    System.Globalization.CultureInfo.InvariantCulture))
                .Should().Contain("bad cast");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static async IAsyncEnumerable<IFeature> Features(params IFeature[] features)
    {
        foreach (var feature in features)
        {
            yield return feature;
        }

        await Task.CompletedTask;
    }
}
