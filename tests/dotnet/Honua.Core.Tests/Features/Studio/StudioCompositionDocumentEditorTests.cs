// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Features.Shared.Services;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Studio;

public sealed class StudioCompositionDocumentEditorTests
{
    private const int InlineStyleLengthLimit = 262_144;

    private const string AuthorizedMapSpec = """
        {"version":8,"sources":{"parcels":{"type":"geojson","data":{}}},
         "layers":[{"id":"parcels","type":"fill","source":"parcels","paint":{"fill-color":"#000000"}}]}
        """;

    [UnitTest]
    public void RemoveLayer_WhenTheDocumentDeclaresNoPortableMembers_LeavesThemUndeclared()
    {
        var envelope = Envelope("""{"layers":[{"id":"parcels","sourceId":"parcels"}]}""");

        var body = Body(StudioCompositionDocumentEditor.RemoveLayer(envelope, "parcels"));

        Assert.False(body.ContainsKey("sourceBindings"));
        Assert.False(body.ContainsKey("styleRefs"));
        Assert.False(body.ContainsKey("mapSpec"));
    }

    [UnitTest]
    public void RemoveLayer_WhenTheMapSpecHasNoRenderLayers_LeavesTheMapSpecUntouched()
    {
        var envelope = Envelope("""
            {"layers":[{"id":"parcels","sourceId":"parcels"}],
             "mapSpec":{"version":8,"sources":{"parcels":{"type":"geojson"}},"metadata":{"honua:compositionLayerIds":["parcels"]}}}
            """);

        var body = Body(StudioCompositionDocumentEditor.RemoveLayer(envelope, "parcels"));

        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""{"version":8,"sources":{"parcels":{"type":"geojson"}},"metadata":{"honua:compositionLayerIds":["parcels"]}}"""),
            body["mapSpec"]));
    }

    [UnitTest]
    public void RemoveLayer_WhenAControlStillUsesTheSourceOnlyThatLayerBinds_ThrowsConflict()
    {
        var envelope = Envelope("""
            {"layers":[{"id":"parcels","sourceId":"parcel-source"}],
             "sourceBindings":[{"sourceId":"parcel-source","protocol":"ogc_features","locator":{}}],
             "controls":[{"id":"zone-filter","kind":"select","sourceId":"parcel-source"}]}
            """);

        var thrown = Assert.Throws<StudioCompositionConflictException>(
            () => StudioCompositionDocumentEditor.RemoveLayer(envelope, "parcels"));

        Assert.Equal(
            "Cannot remove layer 'parcels' because control 'zone-filter' still uses sourceId 'parcel-source'. "
            + "Remove or rebind the control first.",
            thrown.Message);
    }

    [UnitTest]
    public void RemoveLayer_WhenAWidgetStillUsesTheSourceOnlyThatLayerBinds_ThrowsConflict()
    {
        var envelope = Envelope("""
            {"layers":[{"id":"parcels","sourceId":"parcel-source"}],
             "sourceBindings":[{"sourceId":"parcel-source","protocol":"ogc_features","locator":{}}],
             "widgets":[{"id":"area-chart","kind":"chart","sourceId":"parcel-source"}]}
            """) with { Family = StudioPackageFamily.App };

        var thrown = Assert.Throws<StudioCompositionConflictException>(
            () => StudioCompositionDocumentEditor.RemoveLayer(envelope, "parcels"));

        Assert.Equal(
            "Cannot remove layer 'parcels' because widget 'area-chart' still uses sourceId 'parcel-source'. "
            + "Remove or rebind the widget first.",
            thrown.Message);
    }

    [UnitTest]
    public void RemoveLayer_DropsEveryBindingNoRemainingLayerUses()
    {
        var envelope = Envelope("""
            {"layers":[{"id":"parcels","sourceId":"parcels"},{"id":"roads","sourceId":"roads"}],
             "sourceBindings":[
               {"sourceId":"parcels","protocol":"ogc_features","locator":{}},
               {"sourceId":"roads","protocol":"ogc_features","locator":{}},
               {"sourceId":"stale","protocol":"ogc_features","locator":{}}]}
            """);

        var body = Body(StudioCompositionDocumentEditor.RemoveLayer(envelope, "parcels"));

        var remaining = body["sourceBindings"]!.AsArray().Select(binding => binding!["sourceId"]!.GetValue<string>());
        Assert.Equal(["roads"], remaining);
    }

    [UnitTest]
    public void SetLayerInlineStyle_WhenTheLayerAlreadyHasOne_ReplacesItAndKeepsOtherStyles()
    {
        var envelope = Envelope("""
            {"layers":[{"id":"parcels"}],
             "styleRefs":[{"styleId":"honua-inline:parcels","body":{"parcels":{"paint":{"fill-color":"#000000"}}}},
                          {"styleId":"catalog-style"}]}
            """);

        var updated = StudioCompositionDocumentEditor.SetLayerInlineStyle(
            envelope, "parcels", Element("""{"parcels":{"paint":{"fill-color":"#112233"}}}"""));

        var body = Body(updated);
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""
                [{"styleId":"catalog-style"},
                 {"styleId":"honua-inline:parcels","body":{"parcels":{"paint":{"fill-color":"#112233"}}}}]
                """),
            body["styleRefs"]));
        Assert.Equal("honua-inline:parcels", body["layers"]![0]!["styleRef"]!.GetValue<string>());
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("""{"roads":{"paint":{}}}""", "Style overrides must target the selected layer only.")]
    [InlineData("""{"parcels":{"source":"other"}}""", "Style overrides cannot change layer identity or sources.")]
    [InlineData("""["parcels"]""", "Style overrides must be an object.")]
    [InlineData("null", "Style overrides must be an object.")]
    public void SetLayerInlineStyle_WithAnOverrideOutsideTheLayersAppearance_ThrowsConflict(string styleBody, string message)
    {
        var envelope = Envelope("""{"layers":[{"id":"parcels"}]}""");

        var thrown = Assert.Throws<StudioCompositionConflictException>(
            () => StudioCompositionDocumentEditor.SetLayerInlineStyle(envelope, "parcels", Element(styleBody)));

        Assert.Equal(message, thrown.Message);
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("n")]
    [InlineData("\u00e9")]
    [InlineData("\ud83d\ude00")]
    public void SetLayerInlineStyle_AtTheJavaScriptStringLengthLimit_StoresTheStyle(string filler)
    {
        var envelope = Envelope("""{"layers":[{"id":"parcels"}]}""");

        var updated = StudioCompositionDocumentEditor.SetLayerInlineStyle(
            envelope, "parcels", Element(InlineStyleOfLength(InlineStyleLengthLimit, filler)));

        Assert.Equal("honua-inline:parcels", Body(updated)["layers"]![0]!["styleRef"]!.GetValue<string>());
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("n")]
    [InlineData("\u00e9")]
    public void SetLayerInlineStyle_OneCodeUnitOverTheLimit_ThrowsConflict(string filler)
    {
        var envelope = Envelope("""{"layers":[{"id":"parcels"}]}""");

        var thrown = Assert.Throws<StudioCompositionConflictException>(
            () => StudioCompositionDocumentEditor.SetLayerInlineStyle(
                envelope, "parcels", Element(InlineStyleOfLength(InlineStyleLengthLimit + 1, filler))));

        Assert.Equal("Style overrides exceed the 256 KiB limit.", thrown.Message);
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("""{"parcels":{"metadata":{"label":"\ud800"}}}""")]
    [InlineData("""{"\ud800":{"paint":{}}}""")]
    public void SetLayerInlineStyle_WithAParsedLoneSurrogate_ThrowsConflict(string styleBody)
    {
        var envelope = Envelope("""{"layers":[{"id":"parcels"}]}""");

        var thrown = Assert.Throws<StudioCompositionConflictException>(
            () => StudioCompositionDocumentEditor.SetLayerInlineStyle(envelope, "parcels", Element(styleBody)));

        Assert.Equal(UnpairedUtf16SurrogateException.MessageText, thrown.Message);
    }

    [UnitTest]
    public void SetLayerInlineStyle_WhenTheLayerIsMissing_ThrowsNotFound()
    {
        var envelope = Envelope("""{"layers":[]}""");

        Assert.Throws<StudioCompositionNotFoundException>(
            () => StudioCompositionDocumentEditor.SetLayerInlineStyle(envelope, "parcels", Element("""{"parcels":{}}""")));
    }

    [UnitTest]
    public void SetMapStyle_WithoutAnAuthorizedMapStyle_ThrowsConflict()
    {
        var envelope = Envelope("""{"layers":[{"id":"parcels"}]}""");

        var thrown = Assert.Throws<StudioCompositionConflictException>(
            () => StudioCompositionDocumentEditor.SetMapStyle(envelope, Element(AuthorizedMapSpec)));

        Assert.Equal("Initialize the authorized map style before editing it.", thrown.Message);
    }

    [UnitTest]
    public void SetMapStyle_WithARestyledLayer_StoresTheStyleAndClearsStyleDefinitionsAndBindings()
    {
        var envelope = Envelope($$$"""
            {"layers":[{"id":"parcels","sourceId":"parcels","styleRef":"honua-inline:parcels"}],
             "styleRefs":[{"styleId":"honua-inline:parcels","body":{}}],
             "mapSpec":{{{AuthorizedMapSpec}}}}
            """);
        const string restyled = """
            {"version":8,"sources":{"parcels":{"type":"geojson","data":{}}},
             "layers":[{"id":"parcels","type":"fill","source":"parcels","paint":{"fill-color":"#ffffff"}},
                       {"id":"parcel-labels","type":"symbol","source":"parcels"}]}
            """;

        var body = Body(StudioCompositionDocumentEditor.SetMapStyle(envelope, Element(restyled)));

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(restyled), body["mapSpec"]));
        Assert.Empty(body["styleRefs"]!.AsArray());
        Assert.False(body["layers"]![0]!.AsObject().ContainsKey("styleRef"));
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("""{"version":7,"sources":{},"layers":[]}""", "Expected a MapLibre v8 style document.")]
    [InlineData("""{"version":8,"sources":{"parcels":{"type":"vector","url":"x"}},"layers":[]}""",
        "The editor cannot change protected style member 'sources'.")]
    [InlineData("""{"version":8,"sources":{"parcels":{"type":"geojson","data":{}}},"layers":[{"type":"fill"}]}""",
        "Every render layer needs an identity.")]
    [InlineData("""{"version":8,"sources":{"parcels":{"type":"geojson","data":{}}},"layers":[{"id":"parcels","type":"fill","source":"parcels","source-layer":"x"}]}""",
        "Existing render-layer source bindings cannot change.")]
    [InlineData("""{"version":8,"sources":{"parcels":{"type":"geojson","data":{}}},"layers":[{"id":"extra","type":"line","source":"elsewhere"}]}""",
        "Render layers must use an existing authorized source.")]
    [InlineData("""{"version":8,"sources":{"parcels":{"type":"geojson","data":{}}},"layers":[{"id":"background"}]}""",
        "Layer must have a string type")]
    public void SetMapStyle_WithAnEditOutsideTheEditorsAuthority_ThrowsConflict(string mapSpec, string message)
    {
        var thrown = Assert.Throws<StudioCompositionConflictException>(
            () => StudioCompositionDocumentEditor.SetMapStyle(StyledEnvelope(AuthorizedMapSpec), Element(mapSpec)));

        Assert.Equal(message, thrown.Message);
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("""{"version":8,"name":"\ud800","sources":{"parcels":{"type":"geojson","data":{}}},"layers":[{"id":"parcels","type":"fill","source":"parcels"}]}""")]
    [InlineData("""{"\ud800":1,"version":8,"sources":{"parcels":{"type":"geojson","data":{}}},"layers":[{"id":"parcels","type":"fill","source":"parcels"}]}""")]
    public void SetMapStyle_WithAParsedLoneSurrogate_ThrowsConflict(string mapSpec)
    {
        var thrown = Assert.Throws<StudioCompositionConflictException>(
            () => StudioCompositionDocumentEditor.SetMapStyle(StyledEnvelope(AuthorizedMapSpec), Element(mapSpec)));

        Assert.Equal(UnpairedUtf16SurrogateException.MessageText, thrown.Message);
    }

    [UnitTest]
    public void SetMapStyle_WhenTheStyleExceedsOneMebibyte_ThrowsConflict()
    {
        var oversized = $$$$"""
            {"version":8,"name":"{{{{new string('n', 1_048_576)}}}}","sources":{"parcels":{"type":"geojson","data":{}}},"layers":[]}
            """;

        var thrown = Assert.Throws<StudioCompositionConflictException>(
            () => StudioCompositionDocumentEditor.SetMapStyle(StyledEnvelope(AuthorizedMapSpec), Element(oversized)));

        Assert.Equal("Map style exceeds the 1 MiB limit.", thrown.Message);
    }

    [UnitTest]
    public void SetMapStyle_WhenNonAsciiTextExceedsOneMebibyteOfUtf8_ThrowsConflict()
    {
        var oversized = $$$$"""
            {"version":8,"name":"{{{{new string('\u00e9', 524_288)}}}}","sources":{"parcels":{"type":"geojson","data":{}}},"layers":[]}
            """;

        var thrown = Assert.Throws<StudioCompositionConflictException>(
            () => StudioCompositionDocumentEditor.SetMapStyle(StyledEnvelope(AuthorizedMapSpec), Element(oversized)));

        Assert.Equal("Map style exceeds the 1 MiB limit.", thrown.Message);
    }

    [UnitTest]
    public void SetMapStyle_WhenSupplementaryCharactersStayUnderOneMebibyteOfUtf8_StoresTheStyle()
    {
        var emojiName = string.Concat(Enumerable.Repeat("\ud83d\ude00", 250_000));
        var restyled = $$$$"""
            {"version":8,"name":"{{{{emojiName}}}}","sources":{"parcels":{"type":"geojson","data":{}}},
             "layers":[{"id":"parcels","type":"fill","source":"parcels","paint":{"fill-color":"#000000"}}]}
            """;

        var body = Body(StudioCompositionDocumentEditor.SetMapStyle(StyledEnvelope(AuthorizedMapSpec), Element(restyled)));

        Assert.Equal(emojiName, body["mapSpec"]!["name"]!.GetValue<string>());
    }

    [UnitTest]
    public void SetMapStyle_WhenAHonuaSourceHasNoUrl_ThrowsConflict()
    {
        const string honuaSourced = """
            {"version":8,"sources":{"parcels":{"type":"honua-feature-service"}},"layers":[]}
            """;

        var thrown = Assert.Throws<StudioCompositionConflictException>(
            () => StudioCompositionDocumentEditor.SetMapStyle(StyledEnvelope(honuaSourced), Element(honuaSourced)));

        Assert.Equal("Honua source must have a string url", thrown.Message);
    }

    private static string InlineStyleOfLength(int javaScriptLength, string filler)
    {
        const string prefix = "{\"parcels\":{\"metadata\":{\"label\":\"";
        const string suffix = "\"}}}";
        var labelLength = javaScriptLength - prefix.Length - suffix.Length;
        var label = string.Concat(Enumerable.Repeat(filler, labelLength / filler.Length))
            + new string('n', labelLength % filler.Length);
        return prefix + label + suffix;
    }

    private static StudioPackageEnvelope StyledEnvelope(string mapSpec) =>
        Envelope($$$"""{"layers":[{"id":"parcels","sourceId":"parcels"}],"mapSpec":{{{mapSpec}}}}""");

    private static StudioPackageEnvelope Envelope(string body) => new()
    {
        Family = StudioPackageFamily.Map,
        SchemaVersion = "1.0",
        Body = Element(body),
    };

    private static JsonObject Body(StudioPackageEnvelope envelope) =>
        JsonNode.Parse(envelope.Body!.Value.GetRawText())!.AsObject();

    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
