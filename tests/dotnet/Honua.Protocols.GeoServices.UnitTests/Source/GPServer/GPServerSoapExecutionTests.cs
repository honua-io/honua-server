// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.Protocols.GeoServices.GPServer;
using Honua.Protocols.GeoServices.GPServer.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

[Protocol(TestProtocols.GPServer)]
public sealed class GPServerSoapExecutionTests
{
    private const string ArcGis = "http://www.esri.com/schemas/ArcGIS/10.8";

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public void Submission_PositionalValues_PreservePublishedInputNamesAndContext()
    {
        var xml = Submission("""
            <Values><GPValue xsi:type="tns:GPString"><Value>rectangle-wkb</Value></GPValue>
            <GPValue xsi:type="tns:GPLong"><Value>3857</Value></GPValue></Values>
            <EnvironmentValues><PropertyArray><PropertySetProperty><Key>outputCoordinateSystem</Key>
            <Value xsi:type="tns:GPLong"><Value>4326</Value></Value></PropertySetProperty></PropertyArray></EnvironmentValues>
            """);
        var parameters = GPServerSoapExecution.ReadSubmission(xml, TaskInfo());
        parameters.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["wkb"] = "rectangle-wkb",
            ["srid"] = "3857",
            ["env:outSR"] = "4326"
        });
    }

    [Theory]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData("<ToolName>duplicate</ToolName><Values/>")]
    [InlineData("<Values><GPValue xsi:type='tns:GPDouble'><Value>3</Value></GPValue></Values>")]
    [InlineData("<Values><GPValue xsi:type='xsd:GPString'><Value>x</Value></GPValue></Values>")]
    [InlineData("<Values><GPValue xsi:type='tns:GPString' xsi:nil='true'><Value>x</Value></GPValue></Values>")]
    [InlineData("<Values><GPValue xsi:type='tns:GPString'><Value>x</Value><Extra/></GPValue></Values>")]
    [InlineData("<Values/><Unexpected/>")]
    [InlineData("<Values/><Options><DensifyFeatures>true</DensifyFeatures></Options>")]
    [InlineData("<Values/><Options><ReturnData>invalid</ReturnData></Options>")]
    [InlineData("<Values/><Options><TransportType>esriGPTransportTypeEmbedded</TransportType></Options>")]
    [InlineData("<Values/><Options><ReturnData>true</ReturnData><ReturnData>false</ReturnData></Options>")]
    public void Submission_MalformedOrUnsupportedControls_AreRejected(string arguments)
    {
        var act = () => GPServerSoapExecution.ReadSubmission(Submission(arguments), TaskInfo());
        act.Should().Throw<GeoprocessingValidationException>();
    }

    [Theory]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData("<Values/><Recurse>true</Recurse>", "SubmitJob does not accept the 'Recurse' argument.")]
    [InlineData("<Values/><Values/>", "SubmitJob accepts only one 'Values' argument.")]
    [InlineData("<Values/><Options><Densify>false</Densify></Options>", "Options does not accept the 'Densify' argument.")]
    public void Submission_UnrecognisedOrRepeatedArgument_NamesTheElement(string arguments, string expected)
    {
        var act = () => GPServerSoapExecution.ReadSubmission(Submission(arguments), TaskInfo());
        act.Should().Throw<GeoprocessingValidationException>().WithMessage(expected);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public void Submission_UnsetValues_DoNotInventDefaults()
    {
        var parameters = GPServerSoapExecution.ReadSubmission(Submission("""
            <Values><GPValue xsi:type="tns:GPString"/><GPValue xsi:type="tns:GPLong" xsi:nil="true"/></Values>
            """), TaskInfo());
        parameters.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public void Submission_ArcPyDefaultEnvironment_IsNeutralForEveryTaskKind()
    {
        foreach (var task in new[] { TaskInfo(), FeatureTaskInfo() })
        {
            var xml = Submission("<Values/>" + GPServerSoapRequestFixtures.ArcPyDefaultControls);
            task.ExecutionType = GPServerExecutionPolicy.AsynchronousExecutionType;
            GPServerSoapExecution.ReadSubmission(xml, task).Should().BeEmpty();
            // A changed setting must reach the shared environment validator rather
            // than disappear with the client's defaults.
            xml.Descendants("PropertySetProperty").Single(property => property.Element("Key")!.Value == "nodata")
                .Element("Value")!.Element("Value")!.Value = "MAXIMUM";
            GPServerSoapExecution.ReadSubmission(xml, task)["env:nodata"].Should().Be("MAXIMUM");
            // A changed random seed has no scalar environment form and is rejected.
            xml.Descendants("PropertySetProperty").Single(property => property.Element("Key")!.Value == "randomGenerator")
                .Element("Value")!.Element("Value")!.Value = "42";
            var act = () => GPServerSoapExecution.ReadSubmission(xml, task);
            act.Should().Throw<GeoprocessingValidationException>();
        }
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public void Submission_MultiValue_BecomesRestJsonArray()
    {
        var task = new GPTaskInfoResponse
        {
            Name = "Union",
            Parameters =
            [
                new() { Name = "wkbs", DataType = "GPMultiValue:GPString", Direction = "esriGPParameterDirectionInput" },
                new() { Name = "srid", DataType = "GPLong", Direction = "esriGPParameterDirectionInput" }
            ]
        };
        const string members = """<GPValue xsi:type="tns:GPString"><Value>first"</Value></GPValue><GPValue xsi:type="tns:GPString"><Value>second</Value></GPValue>""";
        var parameters = GPServerSoapExecution.ReadSubmission(Submission($"""
            <Values><GPValue xsi:type="tns:GPMultiValue"><MemberDataType>GPString</MemberDataType><Values xsi:type="tns:GPValues">{members}</Values></GPValue>
            <GPValue xsi:type="tns:GPLong"><Value>3857</Value></GPValue></Values>
            """), task);
        JsonSerializer.Deserialize<string[]>(parameters["wkbs"]).Should().Equal("first\"", "second");
        parameters["srid"].Should().Be("3857");

        foreach (var invalid in new[]
                 {
                     members.Replace("tns:GPString\"><Value>second", "tns:GPLong\"><Value>2", StringComparison.Ordinal),
                     members + """<GPValue xsi:type="tns:GPString" xsi:nil="true"/>"""
                 })
        {
            var act = () => GPServerSoapExecution.ReadSubmission(Submission(
                $"""<Values><GPValue xsi:type="tns:GPMultiValue"><Values>{invalid}</Values></GPValue></Values>"""), task);
            act.Should().Throw<GeoprocessingValidationException>();
        }
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public void Submission_ArcPyRecordSet_BecomesTheRestFeatureSetOfTheLiteralFeature()
    {
        var parameters = GPServerSoapExecution.ReadSubmission(
            Submission($"<Values>{ArcPyPolygonRecordSet}<GPValue xsi:type=\"tns:GPString\"><Value>label</Value></GPValue></Values>"),
            FeatureTaskInfo());
        parameters["field"].Should().Be("label");
        using (var featureSet = JsonDocument.Parse(parameters["input"]))
        {
            var root = featureSet.RootElement;
            root.GetProperty("geometryType").GetString().Should().Be("esriGeometryPolygon");
            root.GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(3857);
            root.GetProperty("fields").EnumerateArray().Select(field => field.GetProperty("name").GetString())
                .Should().Equal("OBJECTID", "label", "score", "Shape_Length", "Shape_Area");
            var feature = root.GetProperty("features").EnumerateArray().Should().ContainSingle().Subject;
            var attributes = feature.GetProperty("attributes");
            attributes.GetProperty("OBJECTID").GetInt64().Should().Be(1);
            attributes.GetProperty("label").GetString().Should().Be("input");
            attributes.GetProperty("score").GetDouble().Should().Be(7.5);
            attributes.GetProperty("Shape_Length").ValueKind.Should().Be(JsonValueKind.Null);
            feature.GetProperty("geometry").GetProperty("rings")[0].EnumerateArray()
                .Select(point => (point[0].GetDouble(), point[1].GetDouble()))
                .Should().Equal(LiteralClockwiseRectangle);
        }

        // The same value must satisfy the REST FeatureSet translation used by layer-aware tasks.
        var translated = GPServerEsriInputTranslation.Translate(parameters, new HashSet<string>(["input"]), includeDerivedSrid: false);
        translated.CapabilityMessage.Should().BeNull();
        translated.InputSpatialReference.Should().Be(3857);
        const string geoJsonDataUri = "data:application/geo+json;base64,";
        translated.Inputs["input"].Should().StartWith(geoJsonDataUri);
        using var collection = JsonDocument.Parse(Convert.FromBase64String(translated.Inputs["input"][geoJsonDataUri.Length..]));
        var geoJson = collection.RootElement.GetProperty("features")[0];
        geoJson.GetProperty("properties").GetProperty("score").GetDouble().Should().Be(7.5);
        var ring = geoJson.GetProperty("geometry").GetProperty("coordinates")[0].EnumerateArray()
            .Select(point => (point[0].GetDouble(), point[1].GetDouble())).ToArray();
        ring.Distinct().Should().BeEquivalentTo(LiteralClockwiseRectangle.Distinct());
        Math.Abs(ShoelaceArea(ring)).Should().Be(3 * 4);
    }

    [Theory]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData("""<Values xsi:type="tns:ArrayOfValue"><Value xsi:type="xsd:int">1</Value>""", """<Values xsi:type="tns:ArrayOfValue">""")]
    [InlineData("""<Value xsi:type="xsd:double">7.5</Value>""", """<Value xsi:type="xsd:string">7.5</Value>""")]
    [InlineData("<WKID>102100</WKID><LatestWKID>3857</LatestWKID>", "")]
    [InlineData("<LatestWKID>3857</LatestWKID>", "<LatestWKID>3857</LatestWKID><VCSWKID>5703</VCSWKID>")]
    [InlineData("<HasID>false</HasID><HasZ>false</HasZ>", "<HasID>false</HasID><HasZ>true</HasZ>")]
    [InlineData("<X>3</X><Y>4</Y></Point>", "<X>3</X><Y>4</Y><ID>7</ID></Point>")]
    [InlineData("<Ring xsi:type=\"tns:Ring\">", "<Ring xsi:type=\"tns:Ring\"><SegmentArray/>")]
    [InlineData("<ShapeFieldName>Shape</ShapeFieldName>", "<ShapeFieldName>Shape</ShapeFieldName><ExceededTransferLimit>true</ExceededTransferLimit>")]
    [InlineData("<Type>esriFieldTypeDouble</Type>", "<Type>esriFieldTypeBlob</Type>")]
    [InlineData("<ShapeFieldName>Shape</ShapeFieldName>", "<ShapeFieldName>Geometry</ShapeFieldName>")]
    public void Submission_UnrepresentableRecordSet_IsRejected(string find, string replacement)
    {
        var changed = ArcPyPolygonRecordSet.Replace(find, replacement, StringComparison.Ordinal);
        changed.Should().NotBe(ArcPyPolygonRecordSet);
        var act = () => GPServerSoapExecution.ReadSubmission(Submission($"<Values>{changed}</Values>"), FeatureTaskInfo());
        act.Should().Throw<GeoprocessingValidationException>();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public void Result_RestFeatureSet_EncodesRecordSetThatReadsBackToTheSameFeature()
    {
        const string geoJson = """
            {"type":"FeatureCollection","features":[{"type":"Feature","properties":{"name":"a<&b","score":2.5,"flag":true,"missing":null},
            "geometry":{"type":"Polygon","coordinates":[[[0,0],[3,0],[3,4],[0,4],[0,0]]]}}]}
            """;
        var value = GPServerEsriOutputTranslation.Translate(ArtifactKind.FeatureLayer,
            "data:application/geo+json;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(geoJson)), 3857);
        var result = XElement.Parse(GPServerSoapExecution.BuildResult(
            [new GPResultResponse { ParamName = "outputFeatureLayer", DataType = "GPFeatureRecordSetLayer", Value = value }], []).ToString());

        var output = result.Element("Values")!.Element("GPValue")!;
        output.Attribute(XsiType)!.Value.Should().Be("tns:GPFeatureRecordSetLayer");
        var fields = output.Element("RecordSet")!.Element("Fields")!.Element("FieldArray")!.Elements("Field").ToArray();
        fields.Select(field => field.Element("Name")!.Value).Should().Equal("OBJECTID", "name", "score", "flag", "missing", "Shape");
        fields.Select(field => field.Element("Type")!.Value).Should().Equal("esriFieldTypeOID", "esriFieldTypeString",
            "esriFieldTypeDouble", "esriFieldTypeSmallInteger", "esriFieldTypeString", "esriFieldTypeGeometry");
        var definition = fields[^1].Element("GeometryDef")!;
        definition.Element("GeometryType")!.Value.Should().Be("esriGeometryPolygon");
        definition.Element("HasZ")!.Value.Should().Be("false");
        definition.Element("SpatialReference")!.Attribute(XsiType)!.Value.Should().Be("tns:ProjectedCoordinateSystem");
        definition.Element("SpatialReference")!.Element("WKID")!.Value.Should().Be("3857");
        output.Element("OIDFieldName")!.Value.Should().Be("OBJECTID");
        output.Element("ShapeFieldName")!.Value.Should().Be("Shape");

        var values = output.Descendants("Record").Should().ContainSingle().Subject.Element("Values")!.Elements("Value").ToArray();
        values.Select(cell => cell.Attribute(XsiType)?.Value).Should().Equal("xsd:int", "xsd:string", "xsd:double", "xsd:short", null, "tns:PolygonN");
        values.Take(4).Select(cell => cell.Value).Should().Equal("1", "a<&b", "2.5", "1");
        values[4].Attribute(XName.Get("nil", "http://www.w3.org/2001/XMLSchema-instance"))!.Value.Should().Be("true");
        var polygon = values[5];
        polygon.Element("Extent")!.Elements().Select(bound => bound.Value).Should().Equal("0", "0", "3", "4");
        var ring = polygon.Descendants("Point")
            .Select(point => (double.Parse(point.Element("X")!.Value, CultureInfo.InvariantCulture),
                double.Parse(point.Element("Y")!.Value, CultureInfo.InvariantCulture))).ToArray();
        ring[0].Should().Be(ring[^1]);
        ring.Distinct().Should().BeEquivalentTo(LiteralClockwiseRectangle.Distinct());
        Math.Abs(ShoelaceArea(ring)).Should().Be(3 * 4);

        // ArcPy submits the loaded output back as an input; the round trip keeps values and geometry.
        output.Attribute(XsiType)!.Value = "tns:GPFeatureRecordSetLayer";
        var submitted = GPServerSoapExecution.ReadSubmission(
            Submission($"<Values>{output}<GPValue xsi:type=\"tns:GPString\"><Value>name</Value></GPValue></Values>"), FeatureTaskInfo());
        using var roundTrip = JsonDocument.Parse(submitted["input"]);
        var attributes = roundTrip.RootElement.GetProperty("features")[0].GetProperty("attributes");
        attributes.GetProperty("name").GetString().Should().Be("a<&b");
        attributes.GetProperty("score").GetDouble().Should().Be(2.5);
        attributes.GetProperty("flag").GetInt64().Should().Be(1);
        attributes.GetProperty("missing").ValueKind.Should().Be(JsonValueKind.Null);
        Math.Abs(ShoelaceArea(roundTrip.RootElement.GetProperty("features")[0].GetProperty("geometry").GetProperty("rings")[0]
            .EnumerateArray().Select(point => (point[0].GetDouble(), point[1].GetDouble())).ToArray())).Should().Be(3 * 4);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public void Result_GeographicPointWithZ_EncodesPointNOrdinatesAndCoordinateSystemKind()
    {
        const string geoJson = """{"type":"Feature","properties":{},"geometry":{"type":"Point","coordinates":[10.5,-20.25,7]}}""";
        var value = GPServerEsriOutputTranslation.Translate(ArtifactKind.FeatureLayer,
            "data:application/geo+json;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(geoJson)), 4326);
        var output = GPServerSoapExecution.BuildResult(
            [new GPResultResponse { ParamName = "points", DataType = "GPFeatureRecordSetLayer", Value = value }], []).Element("Values")!.Element("GPValue")!;
        var definition = output.Descendants("GeometryDef").Single();
        definition.Element("HasZ")!.Value.Should().Be("true");
        definition.Element("SpatialReference")!.Attribute(XsiType)!.Value.Should().Be("tns:GeographicCoordinateSystem");
        definition.Element("SpatialReference")!.Element("WKID")!.Value.Should().Be("4326");
        var point = output.Descendants("Value").Single(cell => cell.Attribute(XsiType)?.Value == "tns:PointN");
        point.Elements().Select(ordinate => (ordinate.Name.LocalName, ordinate.Value)).Should().Equal(("X", "10.5"), ("Y", "-20.25"), ("Z", "7"));
    }

    [UnitTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public void Result_DataReferences_UseGdsDataUrlOnlyForRetrievableUrls()
    {
        using var url = JsonDocument.Parse("""{"url":"https://gp.example/rest/services/s/GPServer/Slope/jobs/j/outputs/0?x=1&y=2"}""");
        var output = GPServerSoapExecution.BuildResult(
            [new GPResultResponse { ParamName = "outputRaster", DataType = "GPRasterDataLayer", Value = url.RootElement.Clone() }], [])
            .Element("Values")!.Element("GPValue")!;
        output.Attribute(XsiType)!.Value.Should().Be("tns:GPRasterDataLayer");
        var data = output.Element("Data")!;
        data.Element("TransportType")!.Value.Should().Be("esriGDSTransportTypeUrl");
        data.Element("URL")!.Value.Should().Be("https://gp.example/rest/services/s/GPServer/Slope/jobs/j/outputs/0?x=1&y=2");

        using var inline = JsonDocument.Parse("""{"url":"data:image/tiff;base64,AAAA"}""");
        foreach (var unavailable in new object?[] { inline.RootElement.Clone(), "raster-label", null })
        {
            var act = () => GPServerSoapExecution.BuildResult(
                [new GPResultResponse { ParamName = "outputRaster", DataType = "GPRasterDataLayer", Value = unavailable }], []);
            act.Should().Throw<GeoprocessingValidationException>();
        }
    }

    private static readonly XName XsiType = XName.Get("type", "http://www.w3.org/2001/XMLSchema-instance");

    private static readonly (double X, double Y)[] LiteralClockwiseRectangle = [(0, 0), (0, 4), (3, 4), (3, 0), (0, 0)];

    // Polygon captured from installed ArcPy 3.7.1 SubmitJob (arcpy.AsShape FeatureSet), with its literal values.
    private const string ArcPyPolygonRecordSet = """
        <GPValue xsi:type="tns:GPFeatureRecordSetLayer"><RecordSet xsi:type="tns:RecordSet"><Fields xsi:type="tns:Fields"><FieldArray xsi:type="tns:ArrayOfField">
        <Field xsi:type="tns:Field"><Name>OBJECTID</Name><Type>esriFieldTypeOID</Type><IsNullable>false</IsNullable><Length>4</Length><Precision>0</Precision><Scale>0</Scale><Required>true</Required><Editable>false</Editable><AliasName>OBJECTID</AliasName></Field>
        <Field xsi:type="tns:Field"><Name>label</Name><Type>esriFieldTypeString</Type><IsNullable>true</IsNullable><Length>32</Length><Precision>0</Precision><Scale>0</Scale><AliasName>label</AliasName></Field>
        <Field xsi:type="tns:Field"><Name>score</Name><Type>esriFieldTypeDouble</Type><IsNullable>true</IsNullable><Length>8</Length><Precision>0</Precision><Scale>0</Scale><AliasName>score</AliasName></Field>
        <Field xsi:type="tns:Field"><Name>Shape</Name><Type>esriFieldTypeGeometry</Type><IsNullable>true</IsNullable><Length>0</Length><Precision>0</Precision><Scale>0</Scale>
        <GeometryDef xsi:type="tns:GeometryDef"><AvgNumPoints>0</AvgNumPoints><GeometryType>esriGeometryPolygon</GeometryType><HasM>false</HasM><HasZ>false</HasZ>
        <SpatialReference xsi:type="tns:ProjectedCoordinateSystem"><WKT>PROJCS["WGS_1984_Web_Mercator_Auxiliary_Sphere"]</WKT><XOrigin>-20037700</XOrigin><YOrigin>-30241100</YOrigin><XYScale>10000</XYScale><ZOrigin>-100000</ZOrigin><ZScale>10000</ZScale><MOrigin>-100000</MOrigin><MScale>10000</MScale><XYTolerance>0.001</XYTolerance><ZTolerance>0.001</ZTolerance><MTolerance>0.001</MTolerance><HighPrecision>true</HighPrecision><WKID>102100</WKID><LatestWKID>3857</LatestWKID></SpatialReference><GridSize0>0</GridSize0></GeometryDef><AliasName>Shape</AliasName></Field>
        <Field xsi:type="tns:Field"><Name>Shape_Length</Name><Type>esriFieldTypeDouble</Type><IsNullable>true</IsNullable><Length>8</Length><Precision>0</Precision><Scale>0</Scale><Required>true</Required><Editable>false</Editable></Field>
        <Field xsi:type="tns:Field"><Name>Shape_Area</Name><Type>esriFieldTypeDouble</Type><IsNullable>true</IsNullable><Length>8</Length><Precision>0</Precision><Scale>0</Scale><Required>true</Required><Editable>false</Editable></Field>
        </FieldArray></Fields><Records xsi:type="tns:ArrayOfRecord"><Record xsi:type="tns:Record"><Values xsi:type="tns:ArrayOfValue"><Value xsi:type="xsd:int">1</Value><Value xsi:type="xsd:string">input</Value><Value xsi:type="xsd:double">7.5</Value>
        <Value xsi:type="tns:PolygonN"><HasID>false</HasID><HasZ>false</HasZ><HasM>false</HasM><Extent xsi:type="tns:EnvelopeN"><XMin>0</XMin><YMin>0</YMin><XMax>3</XMax><YMax>4</YMax></Extent>
        <RingArray xsi:type="tns:ArrayOfRing"><Ring xsi:type="tns:Ring"><PointArray xsi:type="tns:ArrayOfPoint"><Point xsi:type="tns:PointN"><X>0</X><Y>0</Y></Point><Point xsi:type="tns:PointN"><X>0</X><Y>4</Y></Point><Point xsi:type="tns:PointN"><X>3</X><Y>4</Y></Point><Point xsi:type="tns:PointN"><X>3</X><Y>0</Y></Point><Point xsi:type="tns:PointN"><X>0</X><Y>0</Y></Point></PointArray></Ring></RingArray><KnownSimple>true</KnownSimple></Value>
        <Value xsi:nil="true" /><Value xsi:nil="true" /></Values></Record></Records></RecordSet><OIDFieldName>OBJECTID</OIDFieldName><ShapeFieldName>Shape</ShapeFieldName></GPValue>
        """;

    // Independent planar area of a closed ring; sign encodes orientation.
    private static double ShoelaceArea((double X, double Y)[] ring)
        => Enumerable.Range(0, ring.Length - 1).Sum(index => (ring[index].X * ring[index + 1].Y) - (ring[index + 1].X * ring[index].Y)) / 2;

    private static GPTaskInfoResponse FeatureTaskInfo() => new()
    {
        Name = "AttributeFilter",
        Parameters =
        [
            new() { Name = "input", DataType = "GPFeatureRecordSetLayer", Direction = "esriGPParameterDirectionInput" },
            new() { Name = "field", DataType = "GPString", Direction = "esriGPParameterDirectionInput" },
            new() { Name = "outputFeatureLayer", DataType = "GPFeatureRecordSetLayer", Direction = "esriGPParameterDirectionOutput" }
        ]
    };

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public void Result_ScalarArtifact_PreservesExactValueAndEscapesXml()
    {
        var result = GPServerSoapExecution.BuildResult(
            [new GPResultResponse { ParamName = "outputScalar", DataType = "GPString", Value = "a<&b" }],
            [new GPJobMessage { Type = "esriJobMessageTypeWarning", Description = "warning<&" }]);
        var parsed = XElement.Parse(result.ToString());
        parsed.Element("Values")!.Element("GPValue")!.Element("Value")!.Value.Should().Be("a<&b");
        parsed.Element("Messages")!.Element("JobMessage")!.Element("MessageDesc")!.Value.Should().Be("warning<&");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public void ResultNames_PreserveRequestedOrder_AndRejectDuplicates()
    {
        var xml = XElement.Parse("<GetJobResult><JobID>job</JobID><ParameterNames><String>b</String><String>a</String></ParameterNames></GetJobResult>");
        GPServerSoapExecution.ReadResultNames(xml, ["a", "b"]).Should().Equal("b", "a");
        xml.Element("ParameterNames")!.Add(new XElement("String", "b"));
        var act = () => GPServerSoapExecution.ReadResultNames(xml, ["a", "b"]);
        act.Should().Throw<GeoprocessingValidationException>();
    }

    private static XElement Submission(string arguments) => XElement.Parse($"""
        <tns:SubmitJob xmlns:tns="{ArcGis}" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
        <ToolName>Area</ToolName>{arguments}</tns:SubmitJob>
        """);

    private static GPTaskInfoResponse TaskInfo() => new()
    {
        Name = "Area",
        Parameters =
        [
            new() { Name = "wkb", DataType = "GPString", Direction = "esriGPParameterDirectionInput" },
            new() { Name = "srid", DataType = "GPLong", Direction = "esriGPParameterDirectionInput" },
            new() { Name = "outputScalar", DataType = "GPString", Direction = "esriGPParameterDirectionOutput" }
        ]
    };
}
