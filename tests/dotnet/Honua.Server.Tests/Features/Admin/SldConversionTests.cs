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
        // Layer id encodes ruleId-symbolizerIndex, where ruleId always folds in the
        // 0-based ruleIndex so duplicate Rule Names cannot collide on layer id.
        layer.Id.Should().Be("simple-point-0-0");
        // SLD fill is a plain 7-char hex; opacity rides on circle-opacity so MapLibre
        // does not multiply the alpha twice once *-color and *-opacity are combined.
        layer.Paint!["circle-color"].StringValue.Should().Be("#ff7f00");
        layer.Paint["circle-opacity"].NumberValue.Should().Be(0.8d);
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
        // SLD stroke is a plain 7-char hex; stroke-opacity rides on line-opacity so
        // MapLibre does not multiply the alpha twice when combining color and opacity.
        layer.Paint!["line-color"].StringValue.Should().Be("#0044cc");
        layer.Paint["line-opacity"].NumberValue.Should().Be(0.85d);
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

        fill.Paint!["fill-color"].StringValue.Should().Be("#f36f21");
        fill.Paint["fill-opacity"].NumberValue.Should().Be(0.6d);
        fill.Paint.Should().NotContainKey("fill-outline-color",
            "outline lives on the dedicated line layer; setting fill-outline-color would double-stroke the polygon");
        outline.Paint!["line-color"].StringValue.Should().Be("#1f2937");
        outline.Paint["line-width"].NumberValue.Should().Be(1.5d);
        outline.Id.Should().EndWith("-outline");
    }

    [UnitTest]
    public void Parse_PolygonStrokeOnly_ProducesLineLayerWithoutFill()
    {
        // Stroke-only PolygonSymbolizer must not emit a fill layer; otherwise the rendering
        // pipeline defaults missing fill-color to opaque black and paints a solid polygon.
        const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<StyledLayerDescriptor version=""1.0.0"" xmlns=""http://www.opengis.net/sld"">
  <NamedLayer><Name>borders</Name><UserStyle><FeatureTypeStyle><Rule>
    <PolygonSymbolizer>
      <Stroke>
        <CssParameter name=""stroke"">#1f2937</CssParameter>
        <CssParameter name=""stroke-width"">2</CssParameter>
      </Stroke>
    </PolygonSymbolizer>
  </Rule></FeatureTypeStyle></UserStyle></NamedLayer>
</StyledLayerDescriptor>";

        var conversion = SldToMapLibreConverter.Convert(SldParser.Parse(xml));

        conversion.HasErrors.Should().BeFalse();
        conversion.Layers.Should().ContainSingle(l => l.Type == "line",
            "stroke-only polygon must emit exactly one layer (the outline)");
        conversion.Layers.Should().NotContain(l => l.Type == "fill",
            "no Fill in the SLD source means no fill layer should be emitted");
        var outline = conversion.Layers.Single();
        outline.Paint!["line-color"].StringValue.Should().Be("#1f2937");
        outline.Paint["line-width"].NumberValue.Should().Be(2d);
    }

    [UnitTest]
    public void Parse_PolygonFillOnly_ProducesFillLayerWithoutOutline()
    {
        // Fill-only PolygonSymbolizer must not carry a fill-outline-color (no Stroke
        // means no outline). The previous behavior used to copy a default into the fill,
        // here we verify only the fill-color is present.
        const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<StyledLayerDescriptor version=""1.0.0"" xmlns=""http://www.opengis.net/sld"">
  <NamedLayer><Name>fills</Name><UserStyle><FeatureTypeStyle><Rule>
    <PolygonSymbolizer>
      <Fill>
        <CssParameter name=""fill"">#abcdef</CssParameter>
      </Fill>
    </PolygonSymbolizer>
  </Rule></FeatureTypeStyle></UserStyle></NamedLayer>
</StyledLayerDescriptor>";

        var conversion = SldToMapLibreConverter.Convert(SldParser.Parse(xml));

        conversion.HasErrors.Should().BeFalse();
        var fill = conversion.Layers.Single();
        fill.Type.Should().Be("fill");
        fill.Paint!["fill-color"].StringValue.Should().Be("#abcdef");
        fill.Paint.Should().NotContainKey("fill-outline-color");
    }

    [UnitTest]
    public void Parse_RuleLevelVendorOption_EmitsWarningDiagnostic()
    {
        // Rule-level VendorOption (e.g. GeoServer's labelObstacle) must surface as a
        // diagnostic, not be silently dropped — the operator doc promises no construct
        // is dropped quietly.
        const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<StyledLayerDescriptor version=""1.0.0"" xmlns=""http://www.opengis.net/sld""
    xmlns:ogc=""http://www.opengis.net/ogc"">
  <NamedLayer><Name>labels</Name><UserStyle><FeatureTypeStyle><Rule>
    <Name>label-rule</Name>
    <VendorOption name=""labelObstacle"">true</VendorOption>
    <PolygonSymbolizer><Fill><CssParameter name=""fill"">#abcdef</CssParameter></Fill></PolygonSymbolizer>
  </Rule></FeatureTypeStyle></UserStyle></NamedLayer>
</StyledLayerDescriptor>";

        var conversion = SldToMapLibreConverter.Convert(SldParser.Parse(xml));

        conversion.HasErrors.Should().BeFalse();
        conversion.Diagnostics.Should().Contain(d =>
            d.Construct == "VendorOption"
            && d.Message.Contains("labelObstacle")
            && d.Message.StartsWith("Rule VendorOption")
            && d.RuleName == "label-rule");
    }

    [UnitTest]
    public void Parse_TextSymbolizerVendorOption_EmitsWarningDiagnostic()
    {
        // GeoServer commonly attaches partials/autoWrap/repeat options at the
        // TextSymbolizer level; they must surface as diagnostics.
        const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<StyledLayerDescriptor version=""1.0.0"" xmlns=""http://www.opengis.net/sld""
    xmlns:ogc=""http://www.opengis.net/ogc"">
  <NamedLayer><Name>labels</Name><UserStyle><FeatureTypeStyle><Rule>
    <Name>place-labels</Name>
    <TextSymbolizer>
      <Label><ogc:PropertyName>name</ogc:PropertyName></Label>
      <Fill><CssParameter name=""fill"">#000000</CssParameter></Fill>
      <VendorOption name=""autoWrap"">120</VendorOption>
      <VendorOption name=""partials"">true</VendorOption>
    </TextSymbolizer>
  </Rule></FeatureTypeStyle></UserStyle></NamedLayer>
</StyledLayerDescriptor>";

        var conversion = SldToMapLibreConverter.Convert(SldParser.Parse(xml));

        conversion.HasErrors.Should().BeFalse();
        conversion.Diagnostics.Should().Contain(d =>
            d.Construct == "VendorOption"
            && d.Message.Contains("autoWrap")
            && d.Message.StartsWith("TextSymbolizer VendorOption"));
        conversion.Diagnostics.Should().Contain(d =>
            d.Construct == "VendorOption"
            && d.Message.Contains("partials")
            && d.Message.StartsWith("TextSymbolizer VendorOption"));
    }

    [UnitTest]
    public void Parse_HexColorWithOpacity_DoesNotDoubleApplyAlpha()
    {
        // Regression: previously the converter baked SLD opacity into rgba() on the
        // *-color paint property AND set *-opacity, so MapLibre multiplied the alpha
        // twice (e.g. fill-opacity 0.5 with #ff0000 rendered at 0.25 alpha). Color
        // and opacity must be independent paint properties for hex inputs.
        const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<StyledLayerDescriptor version=""1.0.0"" xmlns=""http://www.opengis.net/sld"">
  <NamedLayer><Name>alpha-trio</Name><UserStyle><FeatureTypeStyle>
    <Rule>
      <PointSymbolizer>
        <Graphic>
          <Mark>
            <WellKnownName>circle</WellKnownName>
            <Fill>
              <CssParameter name=""fill"">#ff0000</CssParameter>
              <CssParameter name=""fill-opacity"">0.5</CssParameter>
            </Fill>
          </Mark>
          <Size>8</Size>
        </Graphic>
      </PointSymbolizer>
    </Rule>
    <Rule>
      <LineSymbolizer>
        <Stroke>
          <CssParameter name=""stroke"">#00ff00</CssParameter>
          <CssParameter name=""stroke-opacity"">0.4</CssParameter>
        </Stroke>
      </LineSymbolizer>
    </Rule>
    <Rule>
      <PolygonSymbolizer>
        <Stroke>
          <CssParameter name=""stroke"">#0000ff</CssParameter>
          <CssParameter name=""stroke-opacity"">0.3</CssParameter>
          <CssParameter name=""stroke-width"">2</CssParameter>
        </Stroke>
      </PolygonSymbolizer>
    </Rule>
  </FeatureTypeStyle></UserStyle></NamedLayer>
</StyledLayerDescriptor>";

        var conversion = SldToMapLibreConverter.Convert(SldParser.Parse(xml));

        conversion.HasErrors.Should().BeFalse();

        var circle = conversion.Layers.Single(l => l.Type == "circle");
        circle.Paint!["circle-color"].StringValue.Should().Be("#ff0000",
            "hex color must round-trip without opacity baked into rgba()");
        circle.Paint["circle-opacity"].NumberValue.Should().Be(0.5d);

        // The line layer from the LineSymbolizer and the polygon-stroke-only outline
        // are both `line` layers with no `-outline` suffix (the suffix only applies
        // when the polygon also has a Fill). Distinguish them by color instead.
        var lineLayer = conversion.Layers.Single(l => l.Type == "line"
            && l.Paint != null && l.Paint["line-color"].StringValue == "#00ff00");
        lineLayer.Paint!["line-opacity"].NumberValue.Should().Be(0.4d);

        var polygonStroke = conversion.Layers.Single(l => l.Type == "line"
            && l.Paint != null && l.Paint["line-color"].StringValue == "#0000ff");
        polygonStroke.Paint!["line-opacity"].NumberValue.Should().Be(0.3d);
        polygonStroke.Paint["line-width"].NumberValue.Should().Be(2d);
    }

    [UnitTest]
    public void Parse_PolygonStrokeWithoutWidth_DefaultsToOnePixel()
    {
        // SLD/SE default stroke-width is 1.0 when the CssParameter is omitted; the previous
        // converter required Width.HasValue to emit anything, dropping the outline entirely.
        const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<StyledLayerDescriptor version=""1.0.0"" xmlns=""http://www.opengis.net/sld"">
  <NamedLayer><Name>thin-borders</Name><UserStyle><FeatureTypeStyle><Rule>
    <PolygonSymbolizer>
      <Stroke>
        <CssParameter name=""stroke"">#000000</CssParameter>
      </Stroke>
    </PolygonSymbolizer>
  </Rule></FeatureTypeStyle></UserStyle></NamedLayer>
</StyledLayerDescriptor>";

        var conversion = SldToMapLibreConverter.Convert(SldParser.Parse(xml));

        var outline = conversion.Layers.Single(l => l.Type == "line");
        outline.Paint!["line-width"].NumberValue.Should().Be(1d, "SLD default stroke-width is 1.0 when omitted");
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
        fillLayer.Paint!["fill-color"].StringValue.Should().Be("#3366cc");
        fillLayer.Paint["fill-opacity"].NumberValue.Should().Be(0.5d);
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
    public void Parse_PointStrokeWithOpacity_EmitsCircleStrokeOpacityAsSeparatePaintProperty()
    {
        // Regression: previously the converter baked SLD point Stroke opacity into the
        // circle-stroke-color rgba() because it assumed MapLibre had no
        // circle-stroke-opacity paint property. The MapLibre style spec defines
        // circle-stroke-opacity as a first-class circle paint property; emitting it
        // separately keeps the color and opacity orthogonal and round-trippable.
        const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<StyledLayerDescriptor version=""1.0.0"" xmlns=""http://www.opengis.net/sld"">
  <NamedLayer><Name>points</Name><UserStyle><FeatureTypeStyle><Rule>
    <PointSymbolizer>
      <Graphic>
        <Mark>
          <WellKnownName>circle</WellKnownName>
          <Fill><CssParameter name=""fill"">#ff0000</CssParameter></Fill>
          <Stroke>
            <CssParameter name=""stroke"">#1f2937</CssParameter>
            <CssParameter name=""stroke-opacity"">0.4</CssParameter>
            <CssParameter name=""stroke-width"">2</CssParameter>
          </Stroke>
        </Mark>
        <Size>10</Size>
      </Graphic>
    </PointSymbolizer>
  </Rule></FeatureTypeStyle></UserStyle></NamedLayer>
</StyledLayerDescriptor>";

        var conversion = SldToMapLibreConverter.Convert(SldParser.Parse(xml));

        conversion.HasErrors.Should().BeFalse();
        var layer = conversion.Layers.Single();
        layer.Type.Should().Be("circle");
        layer.Paint!["circle-stroke-color"].StringValue.Should().Be("#1f2937",
            "stroke color must round-trip without opacity baked into rgba()");
        layer.Paint["circle-stroke-opacity"].NumberValue.Should().Be(0.4d);
        layer.Paint["circle-stroke-width"].NumberValue.Should().Be(2d);
    }

    [UnitTest]
    public void Export_CircleStrokeOpacity_RoundTripsThroughSld()
    {
        // The export side must read circle-stroke-opacity and emit a CssParameter
        // name="stroke-opacity" inside the Mark Stroke, matching the SLD/SE convention.
        var layer = new MapLibreStyleLayer
        {
            Id = "stroke-with-opacity",
            Type = "circle",
            Paint = new Dictionary<string, MapLibreExpression>
            {
                ["circle-color"] = new MapLibreExpression("#ff0000"),
                ["circle-radius"] = new MapLibreExpression(5d),
                ["circle-stroke-color"] = new MapLibreExpression("#1f2937"),
                ["circle-stroke-opacity"] = new MapLibreExpression(0.4d),
                ["circle-stroke-width"] = new MapLibreExpression(2d)
            }
        };

        var export = MapLibreToSldConverter.Export(new[] { layer }, "test");

        export.Diagnostics.Should().NotContain(d => d.Severity == SldDiagnosticSeverity.Error);
        export.SldXml.Should().Contain("name=\"stroke-opacity\"")
            .And.Contain(">0.4<");

        var roundTrip = SldToMapLibreConverter.Convert(SldParser.Parse(export.SldXml));
        var roundTripLayer = roundTrip.Layers.Single(l => l.Type == "circle");
        roundTripLayer.Paint!["circle-stroke-opacity"].NumberValue.Should().Be(0.4d);
    }

    [UnitTest]
    public void Parse_ExternalGraphicWithSize_OmitsIconSizeAndEmitsDiagnostic()
    {
        // SLD Graphic Size is in absolute pixels (OGC SE 1.1.0 § 11.3.2); MapLibre
        // icon-size is a scale factor against the sprite's intrinsic size. Without
        // sprite metadata the conversion is lossy, so omit icon-size and emit a
        // diagnostic rather than silently scaling the sprite to NxN times its native size.
        var conversion = ParseAndConvert("external-graphic.xml");

        conversion.HasErrors.Should().BeFalse();
        conversion.Diagnostics.Should().Contain(d =>
            d.Construct == "Graphic.Size"
            && d.Severity == SldDiagnosticSeverity.Warning);
        var layer = conversion.Layers.Single();
        layer.Type.Should().Be("symbol");
        layer.Layout!["icon-image"].StringValue.Should().Be("https://example.com/sprites/marker.png");
        layer.Layout.Should().NotContainKey("icon-size",
            "icon-size as a scale factor cannot be derived from SLD Graphic Size in pixels without sprite metadata");
    }

    [UnitTest]
    public void Export_IconSize_OmitsSizeAndEmitsDiagnostic()
    {
        // Symmetric to the import side: MapLibre icon-size (scale factor) cannot map
        // back to absolute SLD <Size> without sprite intrinsic dimensions.
        var layer = new MapLibreStyleLayer
        {
            Id = "icon-with-size",
            Type = "symbol",
            Layout = new Dictionary<string, MapLibreExpression>
            {
                ["icon-image"] = new MapLibreExpression("https://example.com/marker.png"),
                ["icon-size"] = new MapLibreExpression(2d)
            }
        };

        var export = MapLibreToSldConverter.Export(new[] { layer }, "test");

        export.Diagnostics.Should().Contain(d => d.Construct == "icon-size");
        export.SldXml.Should().Contain("ExternalGraphic")
            .And.NotContain("<Size", "icon-size is a scale factor; SLD Size requires absolute pixels");
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

    [UnitTest]
    public void Parse_DuplicateRuleNames_ProduceDistinctLayerIds()
    {
        // Regression: SLD allows two Rules to share a Name. The previous converter
        // built layer ids from `{sanitizedRuleName}-{symbolizerIndex}` only, so two
        // matching rule names with matching symbolizer positions both produced the
        // same id. MapLibreStyleNormalizer rejects duplicate ids and the SLD would
        // fail import. Layer ids must always be unique across rules.
        const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<StyledLayerDescriptor version=""1.0.0"" xmlns=""http://www.opengis.net/sld"">
  <NamedLayer><Name>features</Name><UserStyle><FeatureTypeStyle>
    <Rule>
      <Name>shared</Name>
      <PolygonSymbolizer><Fill><CssParameter name=""fill"">#ff0000</CssParameter></Fill></PolygonSymbolizer>
    </Rule>
    <Rule>
      <Name>shared</Name>
      <PolygonSymbolizer><Fill><CssParameter name=""fill"">#00ff00</CssParameter></Fill></PolygonSymbolizer>
    </Rule>
  </FeatureTypeStyle></UserStyle></NamedLayer>
</StyledLayerDescriptor>";

        var conversion = SldToMapLibreConverter.Convert(SldParser.Parse(xml));

        conversion.HasErrors.Should().BeFalse();
        conversion.Layers.Should().HaveCount(2);
        var ids = conversion.Layers.Select(l => l.Id).ToArray();
        ids.Should().OnlyHaveUniqueItems(
            "MapLibreStyleNormalizer rejects duplicate layer ids and admin import would 400 otherwise");
        ids.Should().Contain("shared-0-0").And.Contain("shared-1-0");
    }

    [UnitTest]
    public void Parse_VeryLongRuleName_TruncatesIdentifierAndStaysUnique()
    {
        // Regression: SanitizeIdentifier previously stackalloc'd `char[span.Length]`
        // off the SLD Name element. With the 1 MiB body cap, an attacker-controlled
        // Name could drive a multi-megabyte stack frame. The sanitizer now caps the
        // sanitized identifier; ruleIndex still disambiguates rules whose names
        // collapse to the same prefix after truncation.
        var longA = new string('a', 200);
        var longB = $"{new string('a', 200)}-different-tail";
        var xml = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<StyledLayerDescriptor version=""1.0.0"" xmlns=""http://www.opengis.net/sld"">
  <NamedLayer><Name>features</Name><UserStyle><FeatureTypeStyle>
    <Rule>
      <Name>{longA}</Name>
      <PolygonSymbolizer><Fill><CssParameter name=""fill"">#ff0000</CssParameter></Fill></PolygonSymbolizer>
    </Rule>
    <Rule>
      <Name>{longB}</Name>
      <PolygonSymbolizer><Fill><CssParameter name=""fill"">#00ff00</CssParameter></Fill></PolygonSymbolizer>
    </Rule>
  </FeatureTypeStyle></UserStyle></NamedLayer>
</StyledLayerDescriptor>";

        var conversion = SldToMapLibreConverter.Convert(SldParser.Parse(xml));

        conversion.HasErrors.Should().BeFalse();
        var ids = conversion.Layers.Select(l => l.Id!).ToArray();
        ids.Should().OnlyHaveUniqueItems();
        // Both rule names truncate to the same 64-char prefix; ruleIndex keeps the ids unique.
        ids[0].Should().StartWith("aaaa")
            .And.EndWith("-0-0");
        ids[1].Should().StartWith("aaaa")
            .And.EndWith("-1-0");
    }

    [UnitTest]
    public void Parse_TextSymbolizerFillOpacity_RoundTripsThroughTextOpacity()
    {
        // Regression: TextSymbolizer Fill opacity used to be baked into the
        // text-color rgba(); MapLibre exposes text-opacity as a first-class paint
        // property, so the alpha must ride there. Otherwise MapLibre multiplies the
        // alpha twice (color × opacity) and the export side has nothing to write
        // back into SLD Fill fill-opacity.
        const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<StyledLayerDescriptor version=""1.0.0"" xmlns=""http://www.opengis.net/sld""
    xmlns:ogc=""http://www.opengis.net/ogc"">
  <NamedLayer><Name>labels</Name><UserStyle><FeatureTypeStyle><Rule>
    <Name>place-labels</Name>
    <TextSymbolizer>
      <Label><ogc:PropertyName>name</ogc:PropertyName></Label>
      <Fill>
        <CssParameter name=""fill"">#112233</CssParameter>
        <CssParameter name=""fill-opacity"">0.55</CssParameter>
      </Fill>
    </TextSymbolizer>
  </Rule></FeatureTypeStyle></UserStyle></NamedLayer>
</StyledLayerDescriptor>";

        var conversion = SldToMapLibreConverter.Convert(SldParser.Parse(xml));

        conversion.HasErrors.Should().BeFalse();
        var symbol = conversion.Layers.Single(l => l.Type == "symbol");
        symbol.Paint!["text-color"].StringValue.Should().Be("#112233",
            "text-color must round-trip without opacity baked into rgba()");
        symbol.Paint["text-opacity"].NumberValue.Should().Be(0.55d);
    }

    [UnitTest]
    public void Export_TextOpacity_EmittedAsFillOpacity()
    {
        // Symmetric to import: stored MapLibre text-opacity must round-trip into
        // SLD Fill fill-opacity. Otherwise export silently discards opacity.
        var layer = new MapLibreStyleLayer
        {
            Id = "label",
            Type = "symbol",
            Layout = new Dictionary<string, MapLibreExpression>
            {
                ["text-field"] = new MapLibreExpression("{name}")
            },
            Paint = new Dictionary<string, MapLibreExpression>
            {
                ["text-color"] = new MapLibreExpression("#112233"),
                ["text-opacity"] = new MapLibreExpression(0.55d)
            }
        };

        var export = MapLibreToSldConverter.Export(new[] { layer }, "test");

        export.SldXml.Should().Contain("<CssParameter name=\"fill\">#112233</CssParameter>")
            .And.Contain("<CssParameter name=\"fill-opacity\">0.55</CssParameter>");

        // Round-trip: re-parse the exported SLD and confirm text-opacity comes back.
        var roundTrip = SldToMapLibreConverter.Convert(SldParser.Parse(export.SldXml));
        var symbol = roundTrip.Layers.Single(l => l.Type == "symbol");
        symbol.Paint!["text-opacity"].NumberValue.Should().Be(0.55d);
        symbol.Paint["text-color"].StringValue.Should().Be("#112233");
    }

    [UnitTest]
    public void Parse_PolygonSymbolizer_GraphicFill_WarnsAndDropsFillLayer()
    {
        // Regression: SLD <Fill><GraphicFill>...</GraphicFill></Fill> embeds a tiled
        // pattern that has no portable MapLibre equivalent (fill-pattern requires a
        // sprite). The converter previously emitted a fill layer with empty paint,
        // which the rendering pipeline defaults to opaque black — a solid fill
        // silently substituted for the unsupported pattern. The parser now emits a
        // GraphicFill warning and drops the empty Fill so the converter skips the
        // layer entirely.
        const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<StyledLayerDescriptor version=""1.0.0"" xmlns=""http://www.opengis.net/sld"">
  <NamedLayer><Name>patterns</Name><UserStyle><FeatureTypeStyle><Rule>
    <PolygonSymbolizer>
      <Fill>
        <GraphicFill>
          <Graphic>
            <Mark><WellKnownName>square</WellKnownName></Mark>
          </Graphic>
        </GraphicFill>
      </Fill>
    </PolygonSymbolizer>
  </Rule></FeatureTypeStyle></UserStyle></NamedLayer>
</StyledLayerDescriptor>";

        var conversion = SldToMapLibreConverter.Convert(SldParser.Parse(xml));

        conversion.HasErrors.Should().BeFalse();
        conversion.Layers.Should().BeEmpty(
            "GraphicFill is unsupported and the empty Fill must not produce a default-black fill layer");
        conversion.Diagnostics.Should().Contain(d =>
            d.Construct == "GraphicFill"
            && d.Severity == SldDiagnosticSeverity.Warning,
            "the operator must see the unsupported construct rather than silently rendering a solid fill");
    }

    [UnitTest]
    public void Parse_PolygonSymbolizer_GraphicStroke_WarnsAndDropsLineLayer()
    {
        // Regression: SLD <Stroke><GraphicStroke>...</GraphicStroke></Stroke> embeds
        // a repeating sprite stroke that has no portable MapLibre equivalent
        // (line-pattern requires a sprite). The converter previously emitted a line
        // layer with default 1px stroke. Diagnose the construct and drop the layer.
        const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<StyledLayerDescriptor version=""1.0.0"" xmlns=""http://www.opengis.net/sld"">
  <NamedLayer><Name>patterns</Name><UserStyle><FeatureTypeStyle><Rule>
    <PolygonSymbolizer>
      <Stroke>
        <GraphicStroke>
          <Graphic>
            <Mark><WellKnownName>square</WellKnownName></Mark>
          </Graphic>
        </GraphicStroke>
      </Stroke>
    </PolygonSymbolizer>
  </Rule></FeatureTypeStyle></UserStyle></NamedLayer>
</StyledLayerDescriptor>";

        var conversion = SldToMapLibreConverter.Convert(SldParser.Parse(xml));

        conversion.HasErrors.Should().BeFalse();
        conversion.Layers.Should().BeEmpty(
            "GraphicStroke is unsupported and the empty Stroke must not produce a default-black line layer");
        conversion.Diagnostics.Should().Contain(d =>
            d.Construct == "GraphicStroke"
            && d.Severity == SldDiagnosticSeverity.Warning);
    }

    [UnitTest]
    public void Parse_LineSymbolizer_GraphicStroke_WarnsAndDropsSymbolizer()
    {
        // Regression: a LineSymbolizer with only a GraphicStroke fell back to the
        // empty SldStroke fallback inside ParseLineSymbolizer, so the converter
        // produced a line layer with empty paint and the renderer defaulted to a
        // 1px opaque line. The symbolizer must now be dropped entirely.
        const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<StyledLayerDescriptor version=""1.0.0"" xmlns=""http://www.opengis.net/sld"">
  <NamedLayer><Name>roads</Name><UserStyle><FeatureTypeStyle><Rule>
    <LineSymbolizer>
      <Stroke>
        <GraphicStroke>
          <Graphic>
            <Mark><WellKnownName>circle</WellKnownName></Mark>
          </Graphic>
        </GraphicStroke>
      </Stroke>
    </LineSymbolizer>
  </Rule></FeatureTypeStyle></UserStyle></NamedLayer>
</StyledLayerDescriptor>";

        var conversion = SldToMapLibreConverter.Convert(SldParser.Parse(xml));

        conversion.HasErrors.Should().BeFalse();
        conversion.Layers.Should().BeEmpty();
        conversion.Diagnostics.Should().Contain(d =>
            d.Construct == "GraphicStroke"
            && d.Severity == SldDiagnosticSeverity.Warning);
    }

    [UnitTest]
    public void Parse_PolygonSymbolizer_GraphicFillWithFallbackColor_KeepsCssFillAndWarns()
    {
        // Mixed case: an SLD <Fill> contains BOTH a portable CssParameter and an
        // unsupported GraphicFill. The converter must keep the CssParameter fill
        // (so the layer still renders) but emit a GraphicFill warning so the
        // operator sees the lossy aspect of the conversion.
        const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<StyledLayerDescriptor version=""1.0.0"" xmlns=""http://www.opengis.net/sld"">
  <NamedLayer><Name>patterns</Name><UserStyle><FeatureTypeStyle><Rule>
    <PolygonSymbolizer>
      <Fill>
        <GraphicFill>
          <Graphic><Mark><WellKnownName>square</WellKnownName></Mark></Graphic>
        </GraphicFill>
        <CssParameter name=""fill"">#abcdef</CssParameter>
      </Fill>
    </PolygonSymbolizer>
  </Rule></FeatureTypeStyle></UserStyle></NamedLayer>
</StyledLayerDescriptor>";

        var conversion = SldToMapLibreConverter.Convert(SldParser.Parse(xml));

        conversion.HasErrors.Should().BeFalse();
        var fill = conversion.Layers.Single();
        fill.Type.Should().Be("fill");
        fill.Paint!["fill-color"].StringValue.Should().Be("#abcdef");
        conversion.Diagnostics.Should().Contain(d =>
            d.Construct == "GraphicFill"
            && d.Severity == SldDiagnosticSeverity.Warning,
            "the GraphicFill is still lossy even when a CssParameter fill is present");
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
