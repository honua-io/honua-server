// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Infrastructure.Rendering;
using Honua.Server.Features.Styling.Sld;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Admin;

[Trait("Component", "Admin")]
public sealed class SldConstructsConversionTests
{
    private static readonly string FixtureRoot = Path.Join(
        AppContext.BaseDirectory,
        "TestData",
        "Sld");

    [UnitTest]
    public void Convert_PropertyIsLike_PrefixAndSuffix_FiltersFeatures()
    {
        const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<StyledLayerDescriptor version=""1.0.0"" xmlns=""http://www.opengis.net/sld"" xmlns:ogc=""http://www.opengis.net/ogc"">
  <NamedLayer><Name>like</Name><UserStyle><FeatureTypeStyle>
    <Rule>
      <Name>prefix</Name>
      <ogc:Filter><ogc:PropertyIsLike wildCard=""%"" singleChar=""_"" escapeChar=""\""><ogc:PropertyName>name</ogc:PropertyName><ogc:Literal>San %</ogc:Literal></ogc:PropertyIsLike></ogc:Filter>
      <PolygonSymbolizer><Fill><CssParameter name=""fill"">#abcdef</CssParameter></Fill></PolygonSymbolizer>
    </Rule>
    <Rule>
      <Name>suffix</Name>
      <ogc:Filter><ogc:PropertyIsLike wildCard=""%"" singleChar=""_"" escapeChar=""\""><ogc:PropertyName>name</ogc:PropertyName><ogc:Literal>% Rd</ogc:Literal></ogc:PropertyIsLike></ogc:Filter>
      <PolygonSymbolizer><Fill><CssParameter name=""fill"">#123456</CssParameter></Fill></PolygonSymbolizer>
    </Rule>
  </FeatureTypeStyle></UserStyle></NamedLayer>
</StyledLayerDescriptor>";

        var conversion = SldToMapLibreConverter.Convert(SldParser.Parse(xml));

        conversion.Diagnostics.Should().NotContain(d => d.Construct == "PropertyIsLike");
        var zoom = RenderZoom.At(10);
        var prefixFilter = conversion.Layers[0].Filter!.Value;
        ExpressionEvaluator.Evaluate(prefixFilter, Props("name", "San Jose"), zoom).Should().Be(true);
        ExpressionEvaluator.Evaluate(prefixFilter, Props("name", "Santa Cruz"), zoom).Should().Be(false);
        ExpressionEvaluator.Evaluate(prefixFilter, Props("name", null), zoom).Should().Be(false);

        var suffixFilter = conversion.Layers[1].Filter!.Value;
        ExpressionEvaluator.Evaluate(suffixFilter, Props("name", "Oak Rd"), zoom).Should().Be(true);
        ExpressionEvaluator.Evaluate(suffixFilter, Props("name", "Rd"), zoom).Should().Be(false);
        ExpressionEvaluator.Evaluate(suffixFilter, Props("name", "Oak Rd Ext"), zoom).Should().Be(false);
    }

    [UnitTest]
    public void Convert_PropertyIsLike_ContainsCaseInsensitiveAndSingleChar_FiltersFeatures()
    {
        const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<StyledLayerDescriptor version=""1.0.0"" xmlns=""http://www.opengis.net/sld"" xmlns:ogc=""http://www.opengis.net/ogc"">
  <NamedLayer><Name>like</Name><UserStyle><FeatureTypeStyle>
    <Rule>
      <Name>contains-ci</Name>
      <ogc:Filter><ogc:PropertyIsLike wildCard=""*"" singleChar=""?"" escapeChar=""\"" matchCase=""false""><ogc:PropertyName>name</ogc:PropertyName><ogc:Literal>*Park*</ogc:Literal></ogc:PropertyIsLike></ogc:Filter>
      <PolygonSymbolizer><Fill><CssParameter name=""fill"">#654321</CssParameter></Fill></PolygonSymbolizer>
    </Rule>
    <Rule>
      <Name>single-char</Name>
      <ogc:Filter><ogc:PropertyIsLike wildCard=""%"" singleChar=""_"" escapeChar=""\""><ogc:PropertyName>code</ogc:PropertyName><ogc:Literal>US-__</ogc:Literal></ogc:PropertyIsLike></ogc:Filter>
      <PolygonSymbolizer><Fill><CssParameter name=""fill"">#ff00ff</CssParameter></Fill></PolygonSymbolizer>
    </Rule>
  </FeatureTypeStyle></UserStyle></NamedLayer>
</StyledLayerDescriptor>";

        var conversion = SldToMapLibreConverter.Convert(SldParser.Parse(xml));

        conversion.Diagnostics.Should().NotContain(d => d.Construct == "PropertyIsLike");
        var zoom = RenderZoom.At(10);
        var containsFilter = conversion.Layers[0].Filter!.Value;
        ExpressionEvaluator.Evaluate(containsFilter, Props("name", "Central PARKWAY"), zoom).Should().Be(true);
        ExpressionEvaluator.Evaluate(containsFilter, Props("name", "Main St"), zoom).Should().Be(false);

        var singleCharFilter = conversion.Layers[1].Filter!.Value;
        ExpressionEvaluator.Evaluate(singleCharFilter, Props("code", "US-HI"), zoom).Should().Be(true);
        ExpressionEvaluator.Evaluate(singleCharFilter, Props("code", "US-HAW"), zoom).Should().Be(false);
        ExpressionEvaluator.Evaluate(singleCharFilter, Props("code", "CA-ON"), zoom).Should().Be(false);
    }

    [UnitTest]
    public void Convert_PropertyIsLike_MixedMultiAndSingleWildcards_EmitsWarningAndOmitsRule()
    {
        const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<StyledLayerDescriptor version=""1.0.0"" xmlns=""http://www.opengis.net/sld"" xmlns:ogc=""http://www.opengis.net/ogc"">
  <NamedLayer><Name>like-mixed</Name><UserStyle><FeatureTypeStyle><Rule>
    <Name>mixed-like</Name>
    <ogc:Filter>
      <ogc:PropertyIsLike wildCard=""%"" singleChar=""_"" escapeChar=""\"">
        <ogc:PropertyName>name</ogc:PropertyName>
        <ogc:Literal>%a_b%</ogc:Literal>
      </ogc:PropertyIsLike>
    </ogc:Filter>
    <PolygonSymbolizer><Fill><CssParameter name=""fill"">#abcdef</CssParameter></Fill></PolygonSymbolizer>
  </Rule></FeatureTypeStyle></UserStyle></NamedLayer>
</StyledLayerDescriptor>";

        var conversion = SldToMapLibreConverter.Convert(SldParser.Parse(xml));

        conversion.Layers.Should().BeEmpty();
        conversion.Diagnostics.Should().Contain(d =>
            d.Construct == "PropertyIsLike"
            && d.RuleName == "mixed-like"
            && d.Message.Contains("mixes multi-character and single-character"));
    }

    [UnitTest]
    public void Convert_ElseFilter_ExcludesFeaturesMatchedByEarlierRules()
    {
        const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<StyledLayerDescriptor version=""1.0.0"" xmlns=""http://www.opengis.net/sld"" xmlns:ogc=""http://www.opengis.net/ogc"">
  <NamedLayer><Name>roads</Name><UserStyle><FeatureTypeStyle>
    <Rule>
      <Name>highway</Name>
      <ogc:Filter><ogc:PropertyIsEqualTo><ogc:PropertyName>kind</ogc:PropertyName><ogc:Literal>highway</ogc:Literal></ogc:PropertyIsEqualTo></ogc:Filter>
      <LineSymbolizer><Stroke><CssParameter name=""stroke"">#ff0000</CssParameter></Stroke></LineSymbolizer>
    </Rule>
    <Rule>
      <Name>arterial</Name>
      <ogc:Filter><ogc:PropertyIsEqualTo><ogc:PropertyName>kind</ogc:PropertyName><ogc:Literal>arterial</ogc:Literal></ogc:PropertyIsEqualTo></ogc:Filter>
      <LineSymbolizer><Stroke><CssParameter name=""stroke"">#00ff00</CssParameter></Stroke></LineSymbolizer>
    </Rule>
    <Rule>
      <Name>other</Name>
      <ElseFilter/>
      <LineSymbolizer><Stroke><CssParameter name=""stroke"">#999999</CssParameter></Stroke></LineSymbolizer>
    </Rule>
  </FeatureTypeStyle></UserStyle></NamedLayer>
</StyledLayerDescriptor>";

        var conversion = SldToMapLibreConverter.Convert(SldParser.Parse(xml));

        conversion.Diagnostics.Should().NotContain(d => d.Construct == "ElseFilter");
        conversion.Layers.Should().HaveCount(3);
        var elseFilter = conversion.Layers[2].Filter!.Value;
        var zoom = RenderZoom.At(10);

        ExpressionEvaluator.Evaluate(elseFilter, Props("kind", "highway"), zoom).Should().Be(false);
        ExpressionEvaluator.Evaluate(elseFilter, Props("kind", "arterial"), zoom).Should().Be(false);
        ExpressionEvaluator.Evaluate(elseFilter, Props("kind", "residential"), zoom).Should().Be(true);
        ExpressionEvaluator.Evaluate(elseFilter, Props("kind", null), zoom).Should().Be(true);
    }

    [UnitTest]
    public void Convert_ElseFilter_WithUnsupportedSiblingFilter_EmitsWarningAndOmitsElseRule()
    {
        const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<StyledLayerDescriptor version=""1.0.0"" xmlns=""http://www.opengis.net/sld"" xmlns:ogc=""http://www.opengis.net/ogc"">
  <NamedLayer><Name>roads</Name><UserStyle><FeatureTypeStyle>
    <Rule>
      <Name>spatial-only</Name>
      <ogc:Filter><ogc:BBOX/></ogc:Filter>
      <LineSymbolizer><Stroke><CssParameter name=""stroke"">#ff0000</CssParameter></Stroke></LineSymbolizer>
    </Rule>
    <Rule>
      <Name>fallback</Name>
      <ElseFilter/>
      <LineSymbolizer><Stroke><CssParameter name=""stroke"">#999999</CssParameter></Stroke></LineSymbolizer>
    </Rule>
  </FeatureTypeStyle></UserStyle></NamedLayer>
</StyledLayerDescriptor>";

        var conversion = SldToMapLibreConverter.Convert(SldParser.Parse(xml));

        conversion.Layers.Should().BeEmpty();
        conversion.Diagnostics.Should().Contain(d => d.Construct == "ElseFilter" && d.RuleName == "fallback");
    }

    [UnitTest]
    public void Convert_SeFunctionsFixture_MapsRecodeCategorizeInterpolateLikeAndElse()
    {
        var xml = File.ReadAllText(Path.Join(FixtureRoot, "se-functions-like-else.xml"));
        var conversion = SldToMapLibreConverter.Convert(SldParser.Parse(xml));

        conversion.HasErrors.Should().BeFalse();
        conversion.DetectedVersion.Should().Be(SldVersion.Sld11);
        conversion.Diagnostics.Should().BeEmpty();
        conversion.Layers.Should().HaveCount(2);

        var zoom = RenderZoom.At(10);
        var sanLayer = conversion.Layers[0];
        sanLayer.Paint!["fill-color"].Items![0].StringValue.Should().Be("match");
        ExpressionEvaluator.Evaluate(sanLayer.Paint["fill-color"], Props("zone_code", 1), zoom).Should().Be("#e41a1c");
        ExpressionEvaluator.Evaluate(sanLayer.Paint["fill-color"], Props("zone_code", 99), zoom).Should().Be("#cccccc");

        sanLayer.Paint["fill-opacity"].Items![0].StringValue.Should().Be("interpolate");
        ExpressionEvaluator.EvaluateFloat(sanLayer.Paint["fill-opacity"], Props("density", 50), zoom).Should().BeApproximately(0.55f, 0.01f);

        var elseLayer = conversion.Layers[1];
        elseLayer.Paint!["fill-color"].Items![0].StringValue.Should().Be("step");
        ExpressionEvaluator.Evaluate(elseLayer.Paint["fill-color"], Props("area", 1000), zoom).Should().Be("#41b6c4");
        ExpressionEvaluator.Evaluate(elseLayer.Filter!.Value, Props("name", "San Mateo"), zoom).Should().Be(false);
        ExpressionEvaluator.Evaluate(elseLayer.Filter!.Value, Props("name", "Oakland"), zoom).Should().Be(true);
    }

    [UnitTest]
    public void Convert_OgcFunctionForm_RecodeAndInterpolate_MapsToMapLibreExpressions()
    {
        const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<StyledLayerDescriptor version=""1.0.0"" xmlns=""http://www.opengis.net/sld"" xmlns:ogc=""http://www.opengis.net/ogc"">
  <NamedLayer><Name>metrics</Name><UserStyle><FeatureTypeStyle><Rule>
    <PointSymbolizer><Graphic>
      <Mark>
        <WellKnownName>circle</WellKnownName>
        <Fill><CssParameter name=""fill""><ogc:Function name=""Recode""><ogc:PropertyName>cat</ogc:PropertyName><ogc:Literal>1</ogc:Literal><ogc:Literal>#ff0000</ogc:Literal><ogc:Literal>2</ogc:Literal><ogc:Literal>#00ff00</ogc:Literal></ogc:Function></CssParameter></Fill>
      </Mark>
      <Size><ogc:Function name=""Interpolate""><ogc:PropertyName>mag</ogc:PropertyName><ogc:Literal>0</ogc:Literal><ogc:Literal>4</ogc:Literal><ogc:Literal>10</ogc:Literal><ogc:Literal>20</ogc:Literal><ogc:Literal>numeric</ogc:Literal></ogc:Function></Size>
    </Graphic></PointSymbolizer>
  </Rule></FeatureTypeStyle></UserStyle></NamedLayer>
</StyledLayerDescriptor>";

        var conversion = SldToMapLibreConverter.Convert(SldParser.Parse(xml));

        conversion.Diagnostics.Should().BeEmpty();
        var layer = conversion.Layers.Single();
        var zoom = RenderZoom.At(10);
        layer.Paint!["circle-color"].Items![0].StringValue.Should().Be("match");
        ExpressionEvaluator.Evaluate(layer.Paint["circle-color"], Props("cat", 2), zoom).Should().Be("#00ff00");
        layer.Paint["circle-radius"].Items![0].StringValue.Should().Be("interpolate");
        ExpressionEvaluator.EvaluateFloat(layer.Paint["circle-radius"], Props("mag", 5), zoom).Should().BeApproximately(6f, 0.01f);
    }

    [UnitTest]
    public void Convert_WellKnownMarksAndRotation_ProducesSymbolLayerAndRoundTrips()
    {
        const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<StyledLayerDescriptor version=""1.0.0"" xmlns=""http://www.opengis.net/sld"" xmlns:ogc=""http://www.opengis.net/ogc"">
  <NamedLayer><Name>signs</Name><UserStyle><FeatureTypeStyle><Rule>
    <Name>warning-triangle</Name>
    <PointSymbolizer><Graphic>
      <Mark><WellKnownName>triangle</WellKnownName><Fill><CssParameter name=""fill"">#ffcc00</CssParameter><CssParameter name=""fill-opacity"">0.9</CssParameter></Fill></Mark>
      <Size>24</Size><Rotation>45</Rotation>
    </Graphic></PointSymbolizer>
  </Rule></FeatureTypeStyle></UserStyle></NamedLayer>
</StyledLayerDescriptor>";

        var conversion = SldToMapLibreConverter.Convert(SldParser.Parse(xml));

        conversion.Diagnostics.Should().ContainSingle(d =>
            d.Construct == "Mark.WellKnownName"
            && d.Message.Contains("honua-mark-triangle"));
        var layer = conversion.Layers.Single();
        layer.Type.Should().Be("symbol");
        layer.Layout!["icon-image"].StringValue.Should().Be("honua-mark-triangle");
        layer.Layout["icon-size"].NumberValue.Should().Be(1.5d);
        layer.Layout["icon-rotate"].NumberValue.Should().Be(45d);
        layer.Paint!["icon-color"].StringValue.Should().Be("#ffcc00");

        var exported = MapLibreToSldConverter.Export(conversion.Layers, "signs");
        exported.SldXml.Should().Contain("<WellKnownName>triangle</WellKnownName>")
            .And.Contain("<Size>24</Size>")
            .And.Contain("<Rotation>45</Rotation>");
    }

    [UnitTest]
    public void Convert_TextLabelPlacement_MapsAnchorOffsetRotationAndLinePlacement()
    {
        const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<StyledLayerDescriptor version=""1.0.0"" xmlns=""http://www.opengis.net/sld"" xmlns:ogc=""http://www.opengis.net/ogc"">
  <NamedLayer><Name>labels</Name><UserStyle><FeatureTypeStyle>
    <Rule>
      <TextSymbolizer>
        <Label><ogc:PropertyName>name</ogc:PropertyName></Label>
        <Font><CssParameter name=""font-size"">10</CssParameter></Font>
        <LabelPlacement><PointPlacement><AnchorPoint><AnchorPointX>0.0</AnchorPointX><AnchorPointY>1.0</AnchorPointY></AnchorPoint><Displacement><DisplacementX>10</DisplacementX><DisplacementY>5</DisplacementY></Displacement><Rotation><ogc:PropertyName>angle</ogc:PropertyName></Rotation></PointPlacement></LabelPlacement>
      </TextSymbolizer>
    </Rule>
    <Rule>
      <TextSymbolizer><Label><ogc:PropertyName>road</ogc:PropertyName></Label><LabelPlacement><LinePlacement/></LabelPlacement></TextSymbolizer>
    </Rule>
  </FeatureTypeStyle></UserStyle></NamedLayer>
</StyledLayerDescriptor>";

        var conversion = SldToMapLibreConverter.Convert(SldParser.Parse(xml));

        conversion.Diagnostics.Should().NotContain(d => d.Construct == "LabelPlacement");
        conversion.Layers[0].Layout!["text-anchor"].StringValue.Should().Be("top-left");
        conversion.Layers[0].Layout!["text-offset"].Items![0].NumberValue.Should().Be(1d);
        conversion.Layers[0].Layout!["text-offset"].Items![1].NumberValue.Should().Be(-0.5d);
        conversion.Layers[0].Layout!["text-rotate"].Items![1].StringValue.Should().Be("angle");
        conversion.Layers[1].Layout!["symbol-placement"].StringValue.Should().Be("line");
    }

    [UnitTest]
    public void Convert_Recode_MixedLeadingZeroAndDuplicateKeys_ProducesHomogeneousStringMatch()
    {
        const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<StyledLayerDescriptor version=""1.1.0"" xmlns=""http://www.opengis.net/sld"" xmlns:se=""http://www.opengis.net/se"" xmlns:ogc=""http://www.opengis.net/ogc"">
  <NamedLayer><se:Name>codes</se:Name><UserStyle><se:FeatureTypeStyle><se:Rule>
    <se:PolygonSymbolizer><se:Fill><se:SvgParameter name=""fill"">
      <se:Recode fallbackValue=""#cccccc"">
        <se:LookupValue><ogc:PropertyName>code</ogc:PropertyName></se:LookupValue>
        <se:MapItem><se:Data>01</se:Data><se:Value>#ff0000</se:Value></se:MapItem>
        <se:MapItem><se:Data>01</se:Data><se:Value>#00ff00</se:Value></se:MapItem>
        <se:MapItem><se:Data>true</se:Data><se:Value>#0000ff</se:Value></se:MapItem>
      </se:Recode>
    </se:SvgParameter></se:Fill></se:PolygonSymbolizer>
  </se:Rule></se:FeatureTypeStyle></UserStyle></NamedLayer>
</StyledLayerDescriptor>";

        var conversion = SldToMapLibreConverter.Convert(SldParser.Parse(xml));

        conversion.Diagnostics.Should().Contain(d => d.Construct == "Recode" && d.Message.Contains("Duplicate"));
        var match = conversion.Layers.Single().Paint!["fill-color"].Items!;
        match[0].StringValue.Should().Be("match");
        match[1].Items![0].StringValue.Should().Be("to-string");
        match[2].Kind.Should().Be(MapLibreExpressionKind.String);
        match[2].StringValue.Should().Be("01");
        match[4].Kind.Should().Be(MapLibreExpressionKind.String);
        match[4].StringValue.Should().Be("true");
    }

    [UnitTest]
    public void Export_ImportedLikeAndElseStyle_DoesNotEmitUnfilteredRules()
    {
        var xml = File.ReadAllText(Path.Join(FixtureRoot, "se-functions-like-else.xml"));
        var conversion = SldToMapLibreConverter.Convert(SldParser.Parse(xml));

        var exported = MapLibreToSldConverter.Export(conversion.Layers, "parcels");

        exported.Diagnostics.Should().Contain(d => d.Severity == SldDiagnosticSeverity.Error && d.Construct == "MapLibreLayers");
        exported.SldXml.Should().NotContain("<Rule>");
    }

    [UnitTest]
    public void Convert_EmptyLogicalFilters_EmitDiagnosticsAndOmitRules()
    {
        const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<StyledLayerDescriptor version=""1.0.0"" xmlns=""http://www.opengis.net/sld"" xmlns:ogc=""http://www.opengis.net/ogc"">
  <NamedLayer><Name>empty</Name><UserStyle><FeatureTypeStyle>
    <Rule><Name>empty-and</Name><ogc:Filter><ogc:And/></ogc:Filter><PolygonSymbolizer><Fill><CssParameter name=""fill"">#111111</CssParameter></Fill></PolygonSymbolizer></Rule>
    <Rule><Name>empty-not</Name><ogc:Filter><ogc:Not/></ogc:Filter><PolygonSymbolizer><Fill><CssParameter name=""fill"">#222222</CssParameter></Fill></PolygonSymbolizer></Rule>
  </FeatureTypeStyle></UserStyle></NamedLayer>
</StyledLayerDescriptor>";

        var conversion = SldToMapLibreConverter.Convert(SldParser.Parse(xml));

        conversion.Layers.Should().BeEmpty();
        conversion.Diagnostics.Should().Contain(d => d.Construct == "And" && d.RuleName == "empty-and");
        conversion.Diagnostics.Should().Contain(d => d.Construct == "Not" && d.RuleName == "empty-not");
    }

    private static ImmutableDictionary<string, object?> Props(string key, object? value)
        => ImmutableDictionary<string, object?>.Empty.Add(key, value);
}
