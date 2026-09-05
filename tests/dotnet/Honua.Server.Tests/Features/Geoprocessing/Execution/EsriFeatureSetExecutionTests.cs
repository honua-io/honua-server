// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.Protocols.GeoServices.GPServer;
using Honua.TestKit.Attributes;
using static Honua.Server.Tests.Features.Geoprocessing.Execution.ManagedExecutorTestHarness;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

public sealed class EsriFeatureSetExecutionTests
{
    [UnitTest]
    public async Task Clip_EsriFeatureSets_ExecutesAndReturnsExpectedGeometryAndAttributes()
    {
        var definition = new BuiltInProcessCatalog().GetProcess("overlay.clip")!;
        var parameters = definition.Parameters.Where(parameter => parameter.AcceptsGeoJsonDataUri)
            .Select(parameter => parameter.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var translated = GPServerEsriInputTranslation.Translate(new Dictionary<string, string>
        {
            ["input"] = """
                {"geometryType":"esriGeometryPolygon","spatialReference":{"wkid":4326},"features":[
                  {"attributes":{"name":"kept","amount":12,"missing":null},"geometry":{"rings":[[[0,0],[0,4],[4,4],[4,0],[0,0]]]}},
                  {"attributes":{"name":"outside","amount":99},"geometry":{"rings":[[[10,10],[10,12],[12,12],[12,10],[10,10]]]}}
                ]}
                """,
            ["clip"] = """
                {"geometryType":"esriGeometryPolygon","spatialReference":{"wkid":4326},"features":[
                  {"attributes":{},"geometry":{"rings":[[[2,-1],[2,5],[6,5],[6,-1],[2,-1]]]}}
                ]}
                """
        }, parameters);
        translated.CapabilityMessage.Should().BeNull();
        translated.InputSpatialReference.Should().Be(4326);

        var (status, uri) = await RunAsync(new OverlayClipExecutor(Options()), "overlay.clip",
            ("input", translated.Inputs["input"]), ("clip", translated.Inputs["clip"]));
        status.Should().Be(ExecutionJobStatus.Succeeded);
        var canonical = ReadFeatures(uri!);
        canonical.Should().ContainSingle();
        // Independently derived intersection: [0,4]x[0,4] intersect [2,6]x[-1,5]
        // is [2,4]x[0,4], with area 2*4 = 8. The second square is disjoint.
        canonical[0].Geometry.Area.Should().Be(8);
        canonical[0].Geometry.EnvelopeInternal.MinX.Should().Be(2);
        canonical[0].Geometry.EnvelopeInternal.MaxX.Should().Be(4);
        canonical[0].Geometry.EnvelopeInternal.MinY.Should().Be(0);
        canonical[0].Geometry.EnvelopeInternal.MaxY.Should().Be(4);

        var value = GPServerEsriOutputTranslation.Translate(ArtifactKind.FeatureLayer, uri!, 4326);
        value.GetProperty("geometryType").GetString().Should().Be("esriGeometryPolygon");
        value.GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(4326);
        var feature = value.GetProperty("features").EnumerateArray().Single();
        feature.GetProperty("attributes").GetProperty("name").GetString().Should().Be("kept");
        feature.GetProperty("attributes").GetProperty("amount").GetDouble().Should().Be(12);
        feature.GetProperty("attributes").GetProperty("missing").ValueKind.Should().Be(JsonValueKind.Null);
        var coordinates = feature.GetProperty("geometry").GetProperty("rings")[0].EnumerateArray()
            .Select(position => (position[0].GetDouble(), position[1].GetDouble())).ToArray();
        coordinates.Should().HaveCount(5);
        coordinates.Distinct().Should().BeEquivalentTo(new[] { (2d, 0d), (2d, 4d), (4d, 4d), (4d, 0d) });
    }

    [UnitTest]
    public void Output_PointZAndNullAttributes_PreservesOrdinateAndFieldMetadata()
    {
        const string json = """{"type":"Feature","geometry":{"type":"Point","coordinates":[-100,40,123.5]},"properties":{"name":"point","missing":null}}""";
        var value = GPServerEsriOutputTranslation.Translate(ArtifactKind.FeatureLayer,
            DataUriPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)), 4269);
        var feature = value.GetProperty("features")[0];
        feature.GetProperty("geometry").GetProperty("x").GetDouble().Should().Be(-100);
        feature.GetProperty("geometry").GetProperty("y").GetDouble().Should().Be(40);
        feature.GetProperty("geometry").GetProperty("z").GetDouble().Should().Be(123.5);
        feature.GetProperty("attributes").GetProperty("missing").ValueKind.Should().Be(JsonValueKind.Null);
        value.GetProperty("fields").EnumerateArray().Should().Contain(field =>
            field.GetProperty("name").GetString() == "name" && field.GetProperty("type").GetString() == "esriFieldTypeString");
    }

    [Theory]
    [InlineData(ArtifactKind.File)]
    [InlineData(ArtifactKind.Raster)]
    [InlineData(ArtifactKind.FeatureLayer)]
    public void Output_StoredArtifact_ReturnsEsriUrlObject(ArtifactKind kind)
    {
        var value = GPServerEsriOutputTranslation.Translate(kind, "https://example.test/result", 4326);
        value.GetProperty("url").GetString().Should().Be("https://example.test/result");
    }
}
