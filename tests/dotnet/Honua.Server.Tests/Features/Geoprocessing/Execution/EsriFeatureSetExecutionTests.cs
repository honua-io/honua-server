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
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
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

    [Theory]
    [InlineData(4326)]
    [InlineData(102100)]
    public async Task Project_EsriGeometryWithoutSridInput_ExecutesWithExpectedMercatorOrdinates(int esriWkid)
    {
        // #4033: geometry.project declares fromSrid/toSrid but no 'srid'. The GPServer adapter translates
        // an esriGeometry without adding one (the task declares none), and the spatial reference travels
        // inside the EWKB, which the executor checks against fromSrid. 102100 is Esri's Web Mercator alias.
        var (x, y, fromSrid, toSrid) = esriWkid == 4326
            ? (-118.15, 33.8, 4326, 3857)
            : (MercatorX(-118.15), MercatorY(33.8), 3857, 4326);
        var translated = GPServerEsriInputTranslation.Translate(
            new Dictionary<string, string>
            {
                ["wkb"] = $$"""{"x":{{x.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}},"y":{{y.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}},"spatialReference":{"wkid":{{esriWkid}}}}""",
                ["fromSrid"] = fromSrid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["toSrid"] = toSrid.ToString(System.Globalization.CultureInfo.InvariantCulture)
            },
            featureCollectionParameters: null,
            includeDerivedSrid: DeclaresSrid("geometry.project"));
        translated.CapabilityMessage.Should().BeNull();
        translated.Inputs.Should().NotContainKey("srid");

        var (status, uri) = await RunAsync(
            new GeometryProjectJobExecutor(Options(), NullLogger<GeometryProjectJobExecutor>.Instance),
            "geometry.project",
            [.. translated.Inputs.Select(input => (input.Key, input.Value))]);

        status.Should().Be(ExecutionJobStatus.Succeeded);
        using var document = JsonDocument.Parse(Convert.FromBase64String(uri![DataUriPrefix.Length..]));
        var coordinates = document.RootElement.GetProperty("geometry").GetProperty("coordinates");
        // Independently computed spherical Web Mercator: x = R*lon, y = R*ln(tan(pi/4 + lat/2)).
        var (expectedX, expectedY) = esriWkid == 4326 ? (MercatorX(-118.15), MercatorY(33.8)) : (-118.15, 33.8);
        var tolerance = esriWkid == 4326 ? 1e-3 : 1e-9;
        coordinates[0].GetDouble().Should().BeApproximately(expectedX, tolerance);
        coordinates[1].GetDouble().Should().BeApproximately(expectedY, tolerance);
    }

    [UnitTest]
    public async Task GeometryFormat_EsriGeometryWithoutSridInput_ExecutesAndKeepsTheSpatialReference()
    {
        // #4033: conversion.geometry-format declares no 'srid'; the esriGeometry spatial reference must
        // still reach the executor through the EWKB and come back on the result envelope.
        var translated = GPServerEsriInputTranslation.Translate(
            new Dictionary<string, string>
            {
                ["geometry"] = """{"x":-118.15,"y":33.8,"spatialReference":{"wkid":4326}}""",
                ["target"] = "ewkt"
            },
            featureCollectionParameters: null,
            includeDerivedSrid: DeclaresSrid("conversion.geometry-format"));
        translated.CapabilityMessage.Should().BeNull();
        translated.Inputs.Should().NotContainKey("srid");

        var (status, uri) = await RunAsync(
            new GeometryFormatConvertJobExecutor(Options(), NullLogger<GeometryFormatConvertJobExecutor>.Instance),
            "conversion.geometry-format",
            [.. translated.Inputs.Select(input => (input.Key, input.Value))]);

        status.Should().Be(ExecutionJobStatus.Succeeded);
        const string ScalarPrefix = "data:application/json;base64,";
        using var document = JsonDocument.Parse(Convert.FromBase64String(uri![ScalarPrefix.Length..]));
        document.RootElement.GetProperty("srid").GetInt32().Should().Be(4326);
        var value = document.RootElement.GetProperty("value").GetString()!;
        value.Should().StartWith("SRID=4326;");
        var point = (Point)new WKTReader().Read(value["SRID=4326;".Length..]);
        point.X.Should().Be(-118.15);
        point.Y.Should().Be(33.8);
    }

    private static bool DeclaresSrid(string processId)
        => new BuiltInProcessCatalog().GetProcess(processId)!.Parameters
            .Any(parameter => parameter.Name.Equals("srid", StringComparison.OrdinalIgnoreCase));

    private const double EarthRadius = 6378137d;

    private static double MercatorX(double longitude) => EarthRadius * longitude * Math.PI / 180d;

    private static double MercatorY(double latitude) => EarthRadius * Math.Log(Math.Tan((Math.PI / 4d) + (latitude * Math.PI / 360d)));

    [UnitTest]
    public void Output_PointZAndNullAttributes_PreservesOrdinateAndFieldMetadata()
    {
        const string json = """{"type":"Feature","geometry":{"type":"Point","coordinates":[-100,40,123.5]},"properties":{"name":"point","missing":null}}""";
        var normalized = GPServerOutputReprojection.NormalizeGeoJsonWinding(
            DataUriPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)));
        var value = GPServerEsriOutputTranslation.Translate(ArtifactKind.FeatureLayer, normalized!, 4269);
        var feature = value.GetProperty("features")[0];
        feature.GetProperty("geometry").GetProperty("x").GetDouble().Should().Be(-100);
        feature.GetProperty("geometry").GetProperty("y").GetDouble().Should().Be(40);
        feature.GetProperty("geometry").GetProperty("z").GetDouble().Should().Be(123.5);
        feature.GetProperty("attributes").GetProperty("missing").ValueKind.Should().Be(JsonValueKind.Null);
        value.GetProperty("fields").EnumerateArray().Should().Contain(field =>
            field.GetProperty("name").GetString() == "name" && field.GetProperty("type").GetString() == "esriFieldTypeString");
        value.GetProperty("hasZ").GetBoolean().Should().BeTrue();
        value.GetProperty("hasM").GetBoolean().Should().BeFalse();
    }

    [UnitTest]
    public void Input_FeatureSetLevelZFlag_PreservesPolylineElevation()
    {
        var translated = GPServerEsriInputTranslation.Translate(new Dictionary<string, string>
        {
            ["input"] = """{"hasZ":true,"spatialReference":{"wkid":4326},"features":[{"attributes":{"name":"line"},"geometry":{"paths":[[[1,2,30],[4,5,60]]]}}]}"""
        }, new HashSet<string> { "input" });
        translated.CapabilityMessage.Should().BeNull();
        var coordinates = ReadFeatures(translated.Inputs["input"]).Single().Geometry.Coordinates;
        coordinates.Select(coordinate => (coordinate.X, coordinate.Y, coordinate.Z)).Should()
            .Equal((1d, 2d, 30d), (4d, 5d, 60d));
    }

    [UnitTest]
    public void Input_MeasuredFeatureSet_RejectsRatherThanDroppingMeasures()
    {
        var translated = GPServerEsriInputTranslation.Translate(new Dictionary<string, string>
        {
            ["input"] = """{"hasM":true,"features":[{"attributes":{},"geometry":{"paths":[[[1,2,30],[4,5,60]]]}}]}"""
        }, new HashSet<string> { "input" });
        translated.CapabilityMessage.Should().Contain("Measured FeatureSets");
        translated.Translated.Should().BeFalse();
    }

    [UnitTest]
    public void Input_EquivalentWebMercatorWkids_AreAcceptedAtEveryLevel()
    {
        var translated = GPServerEsriInputTranslation.Translate(new Dictionary<string, string>
        {
            ["input"] = """{"spatialReference":{"wkid":102100},"features":[{"attributes":{},"geometry":{"x":1000,"y":2000,"spatialReference":{"wkid":3857}}}]}""",
            ["merge"] = """{"spatialReference":{"wkid":3857},"features":[{"attributes":{},"geometry":{"x":3000,"y":4000,"spatialReference":{"wkid":102113}}}]}"""
        }, new HashSet<string> { "input", "merge" });
        translated.CapabilityMessage.Should().BeNull();
        translated.InputSpatialReference.Should().Be(3857);
        ReadFeatures(translated.Inputs["input"]).Single().Geometry.Coordinate.X.Should().Be(1000);
        ReadFeatures(translated.Inputs["merge"]).Single().Geometry.Coordinate.Y.Should().Be(4000);
    }

    [UnitTest]
    public void Output_UnavailableArtifactLabel_IsNotAdvertisedAsUrl()
    {
        var value = GPServerEsriOutputTranslation.Translate(ArtifactKind.Raster, "Output raster", 0);
        value.ValueKind.Should().Be(JsonValueKind.String);
        value.GetString().Should().Be("Output raster");
    }

    [UnitTest]
    public void EmptyInput_DeclaredFieldsAndGeometryType_AreRetainedInEmptyResult()
    {
        const string declared = """{"geometryType":"esriGeometryPolygon","fields":[{"name":"name","type":"esriFieldTypeString"}],"features":[]}""";
        var translated = GPServerEsriInputTranslation.Translate(new Dictionary<string, string> { ["input"] = declared }, new HashSet<string> { "input" });
        var schema = GPServerEsriOutputTranslation.DescribeInput(translated.Inputs["input"], declared);
        var result = GPServerEsriOutputTranslation.Translate(ArtifactKind.FeatureLayer, translated.Inputs["input"], 0, schema.GetRawText());
        result.GetProperty("features").GetArrayLength().Should().Be(0);
        result.GetProperty("geometryType").GetString().Should().Be("esriGeometryPolygon");
        result.GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(4326);
        result.GetProperty("fields").EnumerateArray().Should().Contain(field => field.GetProperty("name").GetString() == "name");
    }

    [UnitTest]
    public void Output_HeterogeneousGeometry_ReturnsClassifiedCapabilityFailure()
    {
        const string json = """{"type":"FeatureCollection","features":[{"type":"Feature","properties":{},"geometry":{"type":"Point","coordinates":[1,2]}},{"type":"Feature","properties":{},"geometry":{"type":"LineString","coordinates":[[0,0],[1,1]]}}]}""";
        var action = () => GPServerEsriOutputTranslation.Translate(ArtifactKind.FeatureLayer,
            DataUriPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)), 4326);
        action.Should().Throw<GeoprocessingValidationException>().WithMessage("*single geometry type*");
    }

    [UnitTest]
    public void Input_CanonicalGeoJsonCollection_IsPreservedForInlineReader()
    {
        const string json = """{"type":"FeatureCollection","features":[{"type":"Feature","properties":{"name":"canonical"},"geometry":{"type":"Point","coordinates":[-100,40,12]}}]}""";
        var translated = GPServerEsriInputTranslation.Translate(new Dictionary<string, string> { ["inline"] = json });
        translated.CapabilityMessage.Should().BeNull();
        translated.Translated.Should().BeFalse();
        translated.Inputs["inline"].Should().Be(json);
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
