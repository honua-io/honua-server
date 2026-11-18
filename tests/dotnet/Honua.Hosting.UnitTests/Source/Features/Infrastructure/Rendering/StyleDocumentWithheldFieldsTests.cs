// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Infrastructure.Rendering;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Infrastructure.Rendering;

public class StyleDocumentWithheldFieldsTests
{
    private static readonly HashSet<string> Withheld = new(["notes"], StringComparer.OrdinalIgnoreCase);

    [UnitTest]
    public void MapLibre_DropsLayersFilteredOnAWithheldFieldAndPropertiesReadingOne()
    {
        const string style = """
            {"version":8,"layers":[
              {"id":"classes","type":"circle","paint":{"circle-radius":4,"circle-color":["match",["get","Notes"],"Confidential","#f00","#00f"]}},
              {"id":"legacy","type":"circle","filter":["==","notes","Restricted"]},
              {"id":"labels","type":"symbol","layout":{"text-field":"{notes}","text-size":12}},
              {"id":"plain","type":"circle","paint":{"circle-color":"#123456"}}]}
            """;

        var result = JsonNode.Parse(StyleDocumentWithheldFields.WithoutWithheldMapLibre(style, Withheld))!;

        var layers = result["layers"]!.AsArray().Select(layer => layer!["id"]!.GetValue<string>());
        layers.Should().Equal("classes", "labels", "plain");
        result.ToJsonString().Should().NotContain("Confidential").And.NotContain("Restricted").And.NotContain("notes");
        result["layers"]![0]!["paint"]!["circle-radius"]!.GetValue<int>().Should().Be(4);
        result["layers"]![2]!["paint"]!["circle-color"]!.GetValue<string>().Should().Be("#123456");
    }

    [UnitTest]
    public void MapLibre_WithNothingWithheld_ReturnsTheDocumentUnchanged()
    {
        const string style = """{"version":8,"layers":[{"id":"a","filter":["==",["get","notes"],"x"]}]}""";

        StyleDocumentWithheldFields.WithoutWithheldMapLibre(style, new HashSet<string>()).Should().Be(style);
    }

    [UnitTest]
    public void DrawingInfo_FallsBackToTheDefaultSymbolAndDropsLabelClassesOverAWithheldField()
    {
        const string drawingInfo = """
            {"renderer":{"type":"uniqueValue","field1":"NOTES","defaultSymbol":{"type":"esriSMS","color":[1,2,3,255]},
              "uniqueValueInfos":[{"value":"Confidential","symbol":{"type":"esriSMS"}}]},
             "labelingInfo":[{"labelExpressionInfo":{"expression":"$feature.notes"}},{"labelExpression":"[name]"}]}
            """;

        var result = JsonNode.Parse(StyleDocumentWithheldFields.WithoutWithheldDrawingInfo(drawingInfo, Withheld))!;

        result["renderer"]!["type"]!.GetValue<string>().Should().Be("simple");
        result["renderer"]!["symbol"]!["color"]!.AsArray().Should().HaveCount(4);
        result["labelingInfo"]!.AsArray().Should().ContainSingle();
        result.ToJsonString().Should().NotContain("Confidential").And.NotContain("notes", "labels over a withheld field are dropped");
    }

    [UnitTest]
    public void Sld_DropsRulesFilteredOnAWithheldFieldAndSymbolizersReadingOne()
    {
        const string sld = """
            <?xml version="1.0" encoding="UTF-8"?>
            <StyledLayerDescriptor xmlns="http://www.opengis.net/sld" xmlns:ogc="http://www.opengis.net/ogc"><NamedLayer><UserStyle><FeatureTypeStyle>
              <Rule><Name>secret</Name><ogc:Filter><ogc:PropertyIsEqualTo><ogc:PropertyName>notes</ogc:PropertyName><ogc:Literal>Confidential</ogc:Literal></ogc:PropertyIsEqualTo></ogc:Filter><PointSymbolizer/></Rule>
              <Rule><Name>all</Name><PointSymbolizer/><TextSymbolizer><Label><ogc:PropertyName> notes </ogc:PropertyName></Label></TextSymbolizer></Rule>
            </FeatureTypeStyle></UserStyle></NamedLayer></StyledLayerDescriptor>
            """;

        var result = StyleDocumentWithheldFields.WithoutWithheldSld(sld, Withheld);

        result.Should().StartWith("<?xml").And.Contain("<Name>all</Name>").And.Contain("<PointSymbolizer />");
        result.Should().NotContain("Confidential").And.NotContain("notes").And.NotContain("TextSymbolizer");
    }
}
