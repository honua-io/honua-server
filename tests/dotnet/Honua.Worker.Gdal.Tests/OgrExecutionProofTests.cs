// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Worker.Gdal.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.IO;
using Xunit;
using Xunit.Sdk;

namespace Honua.Worker.Gdal.Tests;

public sealed partial class RasterExecutionProofTests
{
    [Theory]
    [InlineData("survey", true)]
    [InlineData("decoy", false)]
    public async Task Ogr_MultilayerGeoPackage_SelectsLayerAndPreservesGeometryAndAttributes(string layerName, bool matchesOracle)
    {
        using var output = JsonDocument.Parse(await Execute("source.ogr", ("source", Input("survey.gpkg")),
            ("sourceFormat", "GPKG"), ("layerName", layerName)));
        if (matchesOracle)
        {
            AssertSurvey(output.RootElement);
        }
        else
        {
            // A real, valid GeoJSON from the wrong layer must fail the same semantic oracle.
            Action assert = () => AssertSurvey(output.RootElement);
            assert.Should().Throw<XunitException>();
        }
    }

    [Theory]
    [InlineData("missing-layer")]
    [InlineData("-sql")]
    [InlineData(" ")]
    public async Task Ogr_InvalidOrMissingLayer_FailsWithoutPublishingAnotherLayer(string layerName)
    {
        var job = GdalJobFactory.Job("source.ogr", ("source", Input("survey.gpkg")),
            ("sourceFormat", "GPKG"), ("layerName", layerName));
        var context = new RecordingJobExecutionContext(job.OperationId);
        var executor = new GdalVectorSourceReadJobExecutor(_runner, GdalJobFactory.Options(_scratch),
            NullLogger<GdalVectorSourceReadJobExecutor>.Instance);
        var result = await executor.ExecuteAsync(job, context, CancellationToken.None);
        result.Status.Should().Be(ExecutionJobStatus.Failed);
        context.Artifacts.Should().BeEmpty();
    }

    private static void AssertSurvey(JsonElement root)
    {
        root.GetProperty("type").GetString().Should().Be("FeatureCollection");
        root.GetProperty("name").GetString().Should().Be("survey");
        root.GetProperty("crs").GetProperty("properties").GetProperty("name").GetString()
            .Should().Be("urn:ogc:def:crs:OGC:1.3:CRS84");
        var features = root.GetProperty("features").EnumerateArray()
            .OrderBy(f => f.GetProperty("properties").GetProperty("key").GetInt32()).ToArray();
        features.Should().HaveCount(4);
        // Independent literal source coordinates; compare every ordinate, including Z.
        string?[] wkts = ["POINT Z (-155.25 19.5 120.125)", "LINESTRING Z (-156 20 1.25,-155 21 2.5)",
            "POLYGON Z ((-154 18 0,-153 18 1,-153 19 2,-154 18 0))", null];
        string?[] names = ["Kīlauea 日本", null, "area", "no geometry"];
        double?[] readings = [12.5, -3.75, null, 0];
        for (var i = 0; i < features.Length; i++)
        {
            var properties = features[i].GetProperty("properties");
            properties.GetProperty("key").GetInt32().Should().Be(11 + i);
            properties.GetProperty("name").GetString().Should().Be(names[i]);
            if (readings[i] is { } reading)
            {
                properties.GetProperty("reading").GetDouble().Should().Be(reading);
            }
            else
            {
                properties.GetProperty("reading").ValueKind.Should().Be(JsonValueKind.Null);
            }
            var geometry = features[i].GetProperty("geometry");
            if (wkts[i] is not { } wkt)
            {
                geometry.ValueKind.Should().Be(JsonValueKind.Null);
                continue;
            }
            var actual = new GeoJsonReader(NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326),
                new Newtonsoft.Json.JsonSerializerSettings(), dimension: 3)
                .Read<NetTopologySuite.Geometries.Geometry>(geometry.GetRawText());
            var expected = new WKTReader().Read(wkt);
            actual.GeometryType.Should().Be(expected.GeometryType);
            actual.Coordinates.Select(c => (c.X, c.Y, c.Z)).Should()
                .Equal(expected.Coordinates.Select(c => (c.X, c.Y, c.Z)));
        }
    }
}
