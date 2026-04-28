// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Linq;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Infrastructure.Rendering;
using Honua.Server.Features.Infrastructure.Styling.Sld;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Unit tests for the SLD parser and SLD↔MapLibre converters. These verify
/// supported-subset conversion fidelity, diagnostic emission, and security
/// rejection of unsafe XML — independent of the HTTP layer.
/// </summary>
[Trait("Component", "Admin")]
public sealed class SldConversionTests
{
    private static readonly string FixtureRoot = Path.Combine(
        AppContext.BaseDirectory,
        "TestData",
        "Sld");

    [UnitTest]
    public void Parse_PointMarkSld10_ProducesCircleLayerWithFillAndStroke()
    {
        var conversion = ParseAndConvert("point-mark-sld10.xml");

        conversion.HasErrors.Should().BeFalse();
        conversion.DetectedVersion.Should().Be(SldVersion.Sld10);
        conversion.Layers.Should().HaveCount(1);

        var layer = conversion.Layers[0];
        layer.Type.Should().Be("circle");
        layer.Id.Should().Be("simple-point-0");
        layer.Paint!["circle-color"].StringValue.Should().StartWith("rgba(255,127,0");
        layer.Paint["circle-stroke-color"].StringValue.Should().Be("#1f2937");
        layer.Paint["circle-radius"].NumberValue.Should().Be(5d);
    }

    [UnitTest]
    public void Parse_LineSld10_ProducesLineLayerWithDashAndCap()
    {
        var conversion = ParseAndConvert("line-stroke-sld10.xml");

        conversion.HasErrors.Should().BeFalse();
        var layer = conversion.Layers.Single();
        layer.Type.Should().Be("line");
        layer.Paint!["line-color"].StringValue.Should().StartWith("rgba(0,68,204");
        layer.Paint["line-width"].NumberValue.Should().Be(3d);
        layer.Paint["line-dasharray"].Items.Should().NotBeNull();
        layer.Paint["line-dasharray"].Items![0].NumberValue.Should().Be(5d);
        layer.Layout!["line-cap"].StringValue.Should().Be("round");
    }

    [UnitTest]
    public void Parse_PolygonFillStrokeSld10_ProducesFillAndOutlineLayers()
    {
        var conversion = ParseAndConvert("polygon-fill-stroke-sld10.xml");

        conversion.HasErrors.Should().BeFalse();
        conversion.Layers.Should().HaveCount(2);
        var fill = conversion.Layers.First(l => l.Type == "fill");
        var outline = conversion.Layers.First(l => l.Type == "line");

        fill.Paint!["fill-color"].StringValue.Should().StartWith("rgba(243,111,33");
        fill.Paint["fill-outline-color"].StringValue.Should().Be("#1f2937");
        outline.Paint!["line-width"].NumberValue.Should().Be(1.5d);
    }

    [UnitTest]
    public void Parse_TextSld10_ProducesSymbolLayerWithLabelAndHalo()
    {
        var conversion = ParseAndConvert("text-sld10.xml");

        conversion.HasErrors.Should().BeFalse();
        var layer = conversion.Layers.Single();
        layer.Type.Should().Be("symbol");
        layer.Layout!["text-field"].StringValue.Should().Be("{name}");
        layer.Layout["text-size"].NumberValue.Should().Be(12d);
        layer.Paint!["text-color"].StringValue.Should().Be("#000000");
        layer.Paint["text-halo-color"].StringValue.Should().Be("#ffffff");
        layer.Paint["text-halo-width"].NumberValue.Should().Be(1.5d);
    }

    [UnitTest]
    public void Parse_RuleWithFilterAndScale_AppliesZoomAndFilter()
    {
        var conversion = ParseAndConvert("rule-filter-scale-sld10.xml");

        conversion.HasErrors.Should().BeFalse();
        var layer = conversion.Layers.Single();
        layer.MinZoom.Should().NotBeNull();
        layer.MaxZoom.Should().NotBeNull();
        layer.MinZoom!.Value.Should().BeLessThan(layer.MaxZoom!.Value);

        layer.Filter.Should().NotBeNull();
        var filter = layer.Filter!.Value;
        filter.Kind.Should().Be(MapLibreExpressionKind.Array);
        filter.Items.Should().NotBeNull();
        filter.Items![0].StringValue.Should().Be("==");
        var getItems = filter.Items[1].Items;
        getItems.Should().NotBeNull();
        getItems![0].StringValue.Should().Be("get");
        getItems[1].StringValue.Should().Be("kind");
        filter.Items[2].StringValue.Should().Be("highway");
    }

    [UnitTest]
    public void Parse_Sld11WithSeNamespace_DetectsVersionAndPolygonLayers()
    {
        var conversion = ParseAndConvert("sld11-se.xml");

        conversion.DetectedVersion.Should().Be(SldVersion.Sld11);
        conversion.HasErrors.Should().BeFalse();
        var fillLayer = conversion.Layers.First(l => l.Type == "fill");
        fillLayer.Paint!["fill-color"].StringValue.Should().StartWith("rgba(51,102,204");
    }

    [UnitTest]
    public void Parse_VendorOption_EmitsWarningDiagnosticAndContinues()
    {
        var conversion = ParseAndConvert("unsupported-vendor-option.xml");

        conversion.HasErrors.Should().BeFalse();
        conversion.Diagnostics.Should().Contain(d => d.Construct == "VendorOption");
        conversion.Layers.Should().NotBeEmpty();
    }

    [UnitTest]
    public void Parse_RemoteExternalGraphic_EmitsWarningAndDoesNotFetch()
    {
        var conversion = ParseAndConvert("external-graphic.xml");

        conversion.HasErrors.Should().BeFalse();
        conversion.Diagnostics.Should().Contain(d => d.Construct == "ExternalGraphic");
        var layer = conversion.Layers.Single();
        layer.Type.Should().Be("symbol");
        layer.Layout!["icon-image"].StringValue.Should().Be("https://example.com/sprites/marker.png");
    }

    [UnitTest]
    public void Parse_MalformedXml_ThrowsSldParseException()
    {
        var xml = ReadFixture("malformed.xml");

        var act = () => SldParser.Parse(xml);

        act.Should().Throw<SldParseException>();
    }

    [UnitTest]
    public void Parse_XxeAttempt_RejectsViaSecureParser()
    {
        var xml = ReadFixture("xxe-attempt.xml");

        var act = () => SldParser.Parse(xml);

        act.Should().Throw<SldParseException>()
            .WithMessage("*not well-formed*");
    }

    [UnitTest]
    public void Parse_NotSldRoot_ThrowsSldParseException()
    {
        const string xml = "<root xmlns=\"http://example.com\"/>";

        var act = () => SldParser.Parse(xml);

        act.Should().Throw<SldParseException>();
    }

    [UnitTest]
    public void Export_CircleLayer_RoundTripsThroughSld()
    {
        var conversion = ParseAndConvert("point-mark-sld10.xml");
        var export = MapLibreToSldConverter.Export(conversion.Layers, "test");

        export.Diagnostics.Should().NotContain(d => d.Severity == SldDiagnosticSeverity.Error);
        export.SldXml.Should().Contain("PointSymbolizer")
            .And.Contain("Mark")
            .And.Contain("CssParameter name=\"stroke\"")
            .And.Contain("xmlns=\"http://www.opengis.net/sld\"");

        // Round-trip: re-parse the exported SLD and ensure we get a circle layer back.
        var roundTrip = SldParser.Parse(export.SldXml);
        var roundTripConversion = SldToMapLibreConverter.Convert(roundTrip);
        roundTripConversion.Layers.Should().NotBeEmpty();
        roundTripConversion.Layers.Should().Contain(l => l.Type == "circle");
    }

    [UnitTest]
    public void Export_NonLiteralExpression_EmitsDiagnosticAndOmitsProperty()
    {
        var layer = new MapLibreStyleLayer
        {
            Id = "data-driven",
            Type = "circle",
            Paint = new Dictionary<string, MapLibreExpression>
            {
                ["circle-color"] = new MapLibreExpression(new[]
                {
                    new MapLibreExpression("match"),
                    new MapLibreExpression(new[] { new MapLibreExpression("get"), new MapLibreExpression("kind") }),
                    new MapLibreExpression("highway"),
                    new MapLibreExpression("#ff0000"),
                    new MapLibreExpression("#000000")
                }),
                ["circle-radius"] = new MapLibreExpression(5d)
            }
        };

        var export = MapLibreToSldConverter.Export(new[] { layer }, "test");

        export.Diagnostics.Should().Contain(d => d.Construct == "circle-color");
        export.SldXml.Should().NotContain("circle-color");
    }

    [UnitTest]
    public void Convert_AlphaPrefixedHex_NormalizesToRgba()
    {
        const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<StyledLayerDescriptor version=""1.0.0"" xmlns=""http://www.opengis.net/sld"">
  <NamedLayer><Name>colors</Name><UserStyle><FeatureTypeStyle><Rule>
    <PolygonSymbolizer>
      <Fill><CssParameter name=""fill"">#80FF8000</CssParameter></Fill>
    </PolygonSymbolizer>
  </Rule></FeatureTypeStyle></UserStyle></NamedLayer>
</StyledLayerDescriptor>";

        var conversion = SldToMapLibreConverter.Convert(SldParser.Parse(xml));

        var fill = conversion.Layers.Single();
        fill.Paint!["fill-color"].StringValue.Should().Match("rgba(255,128,0,*)");
    }

    [UnitTest]
    public void Convert_FilterOgcFunction_EmitsWarningAndDropsFilter()
    {
        const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<StyledLayerDescriptor version=""1.0.0"" xmlns=""http://www.opengis.net/sld"" xmlns:ogc=""http://www.opengis.net/ogc"">
  <NamedLayer><Name>fn</Name><UserStyle><FeatureTypeStyle><Rule>
    <ogc:Filter><ogc:Function name=""strLength""><ogc:PropertyName>name</ogc:PropertyName></ogc:Function></ogc:Filter>
    <PolygonSymbolizer><Fill><CssParameter name=""fill"">#000000</CssParameter></Fill></PolygonSymbolizer>
  </Rule></FeatureTypeStyle></UserStyle></NamedLayer>
</StyledLayerDescriptor>";

        var conversion = SldToMapLibreConverter.Convert(SldParser.Parse(xml));

        conversion.Diagnostics.Should().Contain(d => d.Construct == "OgcFunction");
        var layer = conversion.Layers.Single();
        layer.Filter.Should().BeNull("OGC Function expressions cannot be represented in MapLibre filters");
    }

    [UnitTest]
    public void Convert_AndFilter_BuildsAllExpression()
    {
        const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<StyledLayerDescriptor version=""1.0.0"" xmlns=""http://www.opengis.net/sld"" xmlns:ogc=""http://www.opengis.net/ogc"">
  <NamedLayer><Name>and</Name><UserStyle><FeatureTypeStyle><Rule>
    <ogc:Filter>
      <ogc:And>
        <ogc:PropertyIsGreaterThan><ogc:PropertyName>pop</ogc:PropertyName><ogc:Literal>1000</ogc:Literal></ogc:PropertyIsGreaterThan>
        <ogc:PropertyIsEqualTo><ogc:PropertyName>kind</ogc:PropertyName><ogc:Literal>city</ogc:Literal></ogc:PropertyIsEqualTo>
      </ogc:And>
    </ogc:Filter>
    <PolygonSymbolizer><Fill><CssParameter name=""fill"">#abcdef</CssParameter></Fill></PolygonSymbolizer>
  </Rule></FeatureTypeStyle></UserStyle></NamedLayer>
</StyledLayerDescriptor>";

        var conversion = SldToMapLibreConverter.Convert(SldParser.Parse(xml));

        var layer = conversion.Layers.First(l => l.Type == "fill");
        layer.Filter.Should().NotBeNull();
        layer.Filter!.Value.Items![0].StringValue.Should().Be("all");
        layer.Filter.Value.Items.Should().HaveCount(3);
    }

    [UnitTest]
    public void Convert_AndFilterWithUnsupportedOperand_DropsEntireFilter()
    {
        // A AND <unsupported> must render unfiltered, not silently narrow to A.
        const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<StyledLayerDescriptor version=""1.0.0"" xmlns=""http://www.opengis.net/sld"" xmlns:ogc=""http://www.opengis.net/ogc"">
  <NamedLayer><Name>and-mixed</Name><UserStyle><FeatureTypeStyle><Rule>
    <ogc:Filter>
      <ogc:And>
        <ogc:PropertyIsEqualTo><ogc:PropertyName>kind</ogc:PropertyName><ogc:Literal>city</ogc:Literal></ogc:PropertyIsEqualTo>
        <ogc:BBOX/>
      </ogc:And>
    </ogc:Filter>
    <PolygonSymbolizer><Fill><CssParameter name=""fill"">#abcdef</CssParameter></Fill></PolygonSymbolizer>
  </Rule></FeatureTypeStyle></UserStyle></NamedLayer>
</StyledLayerDescriptor>";

        var conversion = SldToMapLibreConverter.Convert(SldParser.Parse(xml));

        var layer = conversion.Layers.Single();
        layer.Filter.Should().BeNull("compound filters with an unsupported operand must drop the whole filter to preserve documented unfiltered fallback");
        conversion.Diagnostics.Should().Contain(d => d.Construct == "BBOX");
    }

    [UnitTest]
    public void Convert_OrFilterWithUnsupportedOperand_DropsEntireFilter()
    {
        // A OR <unsupported> must render unfiltered, not silently narrow to A.
        const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<StyledLayerDescriptor version=""1.0.0"" xmlns=""http://www.opengis.net/sld"" xmlns:ogc=""http://www.opengis.net/ogc"">
  <NamedLayer><Name>or-mixed</Name><UserStyle><FeatureTypeStyle><Rule>
    <ogc:Filter>
      <ogc:Or>
        <ogc:PropertyIsEqualTo><ogc:PropertyName>kind</ogc:PropertyName><ogc:Literal>city</ogc:Literal></ogc:PropertyIsEqualTo>
        <ogc:Function name=""strLength""><ogc:PropertyName>name</ogc:PropertyName></ogc:Function>
      </ogc:Or>
    </ogc:Filter>
    <PolygonSymbolizer><Fill><CssParameter name=""fill"">#abcdef</CssParameter></Fill></PolygonSymbolizer>
  </Rule></FeatureTypeStyle></UserStyle></NamedLayer>
</StyledLayerDescriptor>";

        var conversion = SldToMapLibreConverter.Convert(SldParser.Parse(xml));

        var layer = conversion.Layers.Single();
        layer.Filter.Should().BeNull("compound filters with an unsupported operand must drop the whole filter to preserve documented unfiltered fallback");
        conversion.Diagnostics.Should().Contain(d => d.Construct == "OgcFunction");
    }

    [UnitTest]
    public void Convert_PropertyIsLike_EmitsWarningAndRendersUnfiltered()
    {
        // PropertyIsLike has SLD wildcard semantics with no portable MapLibre equivalent.
        // Treat as unsupported rather than silently coercing wildcards into literal equality.
        const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<StyledLayerDescriptor version=""1.0.0"" xmlns=""http://www.opengis.net/sld"" xmlns:ogc=""http://www.opengis.net/ogc"">
  <NamedLayer><Name>like</Name><UserStyle><FeatureTypeStyle><Rule>
    <ogc:Filter>
      <ogc:PropertyIsLike wildCard=""%"" singleChar=""_"" escapeChar=""\\"">
        <ogc:PropertyName>name</ogc:PropertyName>
        <ogc:Literal>San %</ogc:Literal>
      </ogc:PropertyIsLike>
    </ogc:Filter>
    <PolygonSymbolizer><Fill><CssParameter name=""fill"">#abcdef</CssParameter></Fill></PolygonSymbolizer>
  </Rule></FeatureTypeStyle></UserStyle></NamedLayer>
</StyledLayerDescriptor>";

        var conversion = SldToMapLibreConverter.Convert(SldParser.Parse(xml));

        var layer = conversion.Layers.Single();
        layer.Filter.Should().BeNull("PropertyIsLike has no portable MapLibre form; layer must render unfiltered");
        conversion.Diagnostics.Should().Contain(d => d.Construct == "PropertyIsLike");
    }

    [UnitTest]
    public void Export_AnyFilterWithNonLiteralOperand_DropsCompoundFilter()
    {
        // Or-of-(supported, unsupported) must drop the whole exported <Filter> rather than
        // silently narrow it to the supported operand on the export side.
        var unsupportedOperand = new MapLibreExpression(new[]
        {
            new MapLibreExpression("=="),
            new MapLibreExpression(new[]
            {
                new MapLibreExpression("match"),
                new MapLibreExpression(new[] { new MapLibreExpression("get"), new MapLibreExpression("kind") }),
                new MapLibreExpression("highway"),
                new MapLibreExpression("major"),
                new MapLibreExpression("minor")
            }),
            new MapLibreExpression("major")
        });

        var supportedOperand = new MapLibreExpression(new[]
        {
            new MapLibreExpression("=="),
            new MapLibreExpression(new[] { new MapLibreExpression("get"), new MapLibreExpression("kind") }),
            new MapLibreExpression("city")
        });

        var compound = new MapLibreExpression(new[]
        {
            new MapLibreExpression("any"),
            supportedOperand,
            unsupportedOperand
        });

        var layer = new MapLibreStyleLayer
        {
            Id = "or-mixed",
            Type = "fill",
            Filter = compound,
            Paint = new Dictionary<string, MapLibreExpression>
            {
                ["fill-color"] = new MapLibreExpression("#abcdef")
            }
        };

        var export = MapLibreToSldConverter.Export(new[] { layer }, "test");

        export.SldXml.Should().NotContain("<ogc:Or", "compound filter must be dropped, not silently narrowed to the supported operand");
        export.SldXml.Should().NotContain("PropertyIsEqualTo");
    }

    [UnitTest]
    public void Parse_ExceedingMaxBytes_ThrowsSldParseException()
    {
        var huge = new string('A', 1_048_577);
        var xml = $"<?xml version=\"1.0\"?><StyledLayerDescriptor xmlns=\"http://www.opengis.net/sld\"><NamedLayer><Name>{huge}</Name></NamedLayer></StyledLayerDescriptor>";

        var act = () => SldParser.Parse(xml);

        act.Should().Throw<SldParseException>()
            .WithMessage("*exceeds*character limit*");
    }

    private static SldConversionResult ParseAndConvert(string fixtureName)
    {
        var xml = ReadFixture(fixtureName);
        var document = SldParser.Parse(xml);
        return SldToMapLibreConverter.Convert(document);
    }

    private static string ReadFixture(string fixtureName)
    {
        var path = Path.Combine(FixtureRoot, fixtureName);
        return File.ReadAllText(path);
    }
}
