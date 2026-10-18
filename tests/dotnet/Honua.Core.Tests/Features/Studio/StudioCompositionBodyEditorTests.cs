// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Security.Claims;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Core.Features.Styling.Abstractions;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Core.Tests.Features.Studio;

/// <summary>
/// Unit tests for the pure, side-effect-free <see cref="StudioCompositionBodyEditor"/>
/// operations (honua-server#3002) the Studio composition MCP tools patch a
/// draft's envelope through.
/// </summary>
public sealed class StudioCompositionBodyEditorTests
{
    [UnitTest]
    public void ReadBody_WithNullBody_ReturnsEmpty()
    {
        var envelope = BuildEnvelope(StudioPackageFamily.Map, body: null);

        var body = StudioCompositionBodyEditor.ReadBody(envelope);

        Assert.Empty(body.Layers);
        Assert.Empty(body.Widgets);
        Assert.Null(body.View);
    }

    [UnitTest]
    public void ReadBody_WithBodyOmittingCollections_NormalizesThemToEmpty()
    {
        // A present-but-sparse body is NOT the same path as a null body: the source-generated
        // converter assigns only members present in the payload, so `{}` and a view-only document
        // come back with Layers/Widgets NULL rather than with their Array.Empty initializers.
        // Every consumer -- AddLayer's Any, RemoveLayer's Where, the reorder applier's
        // ToDictionary and .Count -- dereferences them directly, so leaving them null turns the
        // next composition edit into an unmapped NullReferenceException.
        using var empty = System.Text.Json.JsonDocument.Parse("{}");
        var body = StudioCompositionBodyEditor.ReadBody(
            BuildEnvelope(StudioPackageFamily.Map, empty.RootElement.Clone()));

        Assert.Empty(body.Layers);
        Assert.Empty(body.Widgets);

        using var viewOnly = System.Text.Json.JsonDocument.Parse("""{"view":{"zoom":8}}""");
        var viewOnlyBody = StudioCompositionBodyEditor.ReadBody(
            BuildEnvelope(StudioPackageFamily.Map, viewOnly.RootElement.Clone()));

        Assert.Empty(viewOnlyBody.Layers);
        Assert.Empty(viewOnlyBody.Widgets);
        Assert.NotNull(viewOnlyBody.View);

        // The point of normalizing: an edit against such a body must work rather than throw.
        var withLayer = StudioCompositionBodyEditor.AddLayer(
            viewOnlyBody,
            new StudioCompositionLayer { Id = "parcels" });

        Assert.Single(withLayer.Layers);
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("{\"interactions\":[null]}")]
    [InlineData("{\"interactions\":[{\"id\":\"i\",\"on\":null,\"do\":{\"ref\":\"map\",\"verb\":\"setViewport\"}}]}")]
    [InlineData("{\"interactions\":[{\"id\":\"i\",\"on\":{\"ref\":\"map\",\"event\":\"viewportChange\"},\"do\":null}]}")]
    [InlineData("{\"layout\":{\"items\":[null]}}")]
    public void ReadBody_WithNullReferenceNodes_ThrowsBodyException(string bodyJson)
    {
        using var document = JsonDocument.Parse(bodyJson);
        var envelope = BuildEnvelope(StudioPackageFamily.Map, document.RootElement.Clone());

        Assert.Throws<StudioCompositionBodyException>(
            () => StudioCompositionBodyEditor.ReadBody(envelope));
    }

    [UnitTest]
    public void ReadBody_ThenWriteBody_PreservesFieldsOutsideTheProjection()
    {
        // StudioCompositionBody is a PROJECTION, not the whole document: a canonical map package
        // also carries mapPackageId/format/status/createdAt/sourceBindings/initialView, which
        // the canonical MapPackage carries. Both round trips that serialize this record back over the
        // stored body -- the collaboration checkpoint applier and the MCP Studio composition tools
        // -- would otherwise DELETE every unmodelled field on the first edit. Silent data loss, not
        // a wedge, so nothing would surface at the time.
        using var document = System.Text.Json.JsonDocument.Parse(
            """
            {
              "mapPackageId": "pkg-1",
              "format": "honua.map/1.0",
              "status": "published",
              "sourceBindings": [{"sourceId": "parcels", "layerId": 7}],
              "initialView": {"zoom": 3},
              "layers": [{"id": "parcels"}]
            }
            """);
        var envelope = BuildEnvelope(StudioPackageFamily.Map, document.RootElement.Clone());

        var body = StudioCompositionBodyEditor.ReadBody(envelope);
        var mutated = StudioCompositionBodyEditor.AddLayer(
            body, new StudioCompositionLayer { Id = "roads" });
        var written = StudioCompositionBodyEditor.WriteBody(envelope, mutated);

        var reread = written.Body!.Value;
        Assert.Equal("pkg-1", reread.GetProperty("mapPackageId").GetString());
        Assert.Equal("honua.map/1.0", reread.GetProperty("format").GetString());
        Assert.Equal("published", reread.GetProperty("status").GetString());
        Assert.Equal(3, reread.GetProperty("initialView").GetProperty("zoom").GetInt32());
        Assert.Equal(
            "parcels",
            reread.GetProperty("sourceBindings")[0].GetProperty("sourceId").GetString());

        // ...and the modelled part still applied.
        Assert.Equal(2, reread.GetProperty("layers").GetArrayLength());
    }

    [UnitTest]
    public void WriteBody_MergingIntoAnExistingObjectBody_NeedsNoSerializerMetadata()
    {
        // The published Honua.Server image builds with
        // JsonSerializerIsReflectionEnabledByDefault=false, so a JsonSerializer call can only use
        // registered source-generated metadata. StudioJsonContext registers none for the merge's
        // Dictionary<string, JsonElement>...
        Assert.Null(StudioJsonContext.Default.GetTypeInfo(typeof(Dictionary<string, JsonElement>)));

        // ...so serializing the merged map threw at runtime for EVERY MCP or checkpoint mutation of
        // an existing object body — the only path this merge exists to serve (honua-server#2999
        // review). Writing the merged object through the JSON writer/DOM needs no metadata, and the
        // returned element must stay readable after the writer's own document is disposed.
        JsonElement written;
        {
            using var document = JsonDocument.Parse(
                """{"mapPackageId":"pkg-1","layers":[{"id":"parcels"}]}""");
            var envelope = BuildEnvelope(StudioPackageFamily.Map, document.RootElement.Clone());
            var mutated = StudioCompositionBodyEditor.SetLayerStyleRef(
                StudioCompositionBodyEditor.ReadBody(envelope), "parcels", "night");
            written = StudioCompositionBodyEditor.WriteBody(envelope, mutated).Body!.Value;
        }

        Assert.Equal("pkg-1", written.GetProperty("mapPackageId").GetString());
        Assert.Equal("night", written.GetProperty("layers")[0].GetProperty("styleRef").GetString());
    }

    [UnitTest]
    public void WriteBody_ThenReadBody_RoundTrips()
    {
        var envelope = BuildEnvelope(StudioPackageFamily.Map, body: null);
        var layer = new StudioCompositionLayer { Id = "parcels", Type = "fill" };
        var body = StudioCompositionBodyEditor.AddLayer(StudioCompositionBody.Empty, layer);

        var written = StudioCompositionBodyEditor.WriteBody(envelope, body);
        var roundTripped = StudioCompositionBodyEditor.ReadBody(written);

        Assert.Single(roundTripped.Layers);
        Assert.Equal("parcels", roundTripped.Layers[0].Id);
    }

    [UnitTest]
    public void AddLayer_WithBeforeId_InsertsBeforeTheNamedLayer()
    {
        var body = StudioCompositionBody.Empty;
        body = StudioCompositionBodyEditor.AddLayer(body, new StudioCompositionLayer { Id = "roads" });
        body = StudioCompositionBodyEditor.AddLayer(body, new StudioCompositionLayer { Id = "parcels" }, beforeId: "roads");

        Assert.Equal(["parcels", "roads"], body.Layers.Select(l => l.Id));
    }

    [UnitTest]
    public void AddLayer_WithUnmatchedBeforeId_Appends()
    {
        var body = StudioCompositionBody.Empty;
        body = StudioCompositionBodyEditor.AddLayer(body, new StudioCompositionLayer { Id = "roads" });
        body = StudioCompositionBodyEditor.AddLayer(body, new StudioCompositionLayer { Id = "parcels" }, beforeId: "no-such-layer");

        Assert.Equal(["roads", "parcels"], body.Layers.Select(l => l.Id));
    }

    [UnitTest]
    public void AddLayer_WithDuplicateId_ThrowsConflict()
    {
        var body = StudioCompositionBodyEditor.AddLayer(StudioCompositionBody.Empty, new StudioCompositionLayer { Id = "parcels" });

        Assert.Throws<StudioCompositionConflictException>(
            () => StudioCompositionBodyEditor.AddLayer(body, new StudioCompositionLayer { Id = "parcels" }));
    }

    [UnitTest]
    public void RemoveLayer_WithMissingId_ThrowsNotFound()
        => Assert.Throws<StudioCompositionNotFoundException>(
            () => StudioCompositionBodyEditor.RemoveLayer(StudioCompositionBody.Empty, "no-such-layer"));

    [UnitTest]
    public void RemoveLayer_WithExistingId_RemovesOnlyThatLayer()
    {
        var body = StudioCompositionBody.Empty;
        body = StudioCompositionBodyEditor.AddLayer(body, new StudioCompositionLayer { Id = "roads" });
        body = StudioCompositionBodyEditor.AddLayer(body, new StudioCompositionLayer { Id = "parcels" });

        body = StudioCompositionBodyEditor.RemoveLayer(body, "roads");

        Assert.Equal(["parcels"], body.Layers.Select(l => l.Id));
    }

    [UnitTest]
    public void RemoveLayer_WhenInteractionReferencesLayer_ThrowsConflict()
    {
        var body = StudioCompositionBodyEditor.AddLayer(
            StudioCompositionBody.Empty, new StudioCompositionLayer { Id = "roads" });
        body = body with
        {
            Interactions =
            [
                new StudioInteraction
                {
                    Id = "select-roads",
                    On = new StudioInteractionEvent { Ref = "layer:roads", Event = "featureSelect" },
                    Do = new StudioInteractionAction { Ref = "map", Verb = "setViewport" },
                }
            ]
        };

        Assert.Throws<StudioCompositionConflictException>(
            () => StudioCompositionBodyEditor.RemoveLayer(body, "roads"));
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("roads")]
    [InlineData("roads-source")]
    public void RemoveLayer_WhenControlUsesItsLayerOrSourceId_ThrowsConflict(string controlSourceId)
    {
        var body = StudioCompositionBodyEditor.AddLayer(
            StudioCompositionBody.Empty,
            new StudioCompositionLayer { Id = "roads", SourceId = "roads-source" });
        body = StudioCompositionBodyEditor.AddControl(
            body,
            new StudioCompositionControl { Id = "road-filter", Kind = "filterSelect", SourceId = controlSourceId });

        var error = Assert.Throws<StudioCompositionConflictException>(
            () => StudioCompositionBodyEditor.RemoveLayer(body, "roads"));

        Assert.Contains("road-filter", error.Message, StringComparison.Ordinal);
        Assert.Contains(controlSourceId, error.Message, StringComparison.Ordinal);
    }

    [UnitTest]
    public void RemoveLayer_WhenAnotherLayerDeclaresTheControlSource_RemovesOnlyRequestedLayer()
    {
        var body = StudioCompositionBody.Empty with
        {
            Layers =
            [
                new StudioCompositionLayer { Id = "roads", SourceId = "shared-source" },
                new StudioCompositionLayer { Id = "roads-labels", SourceId = "shared-source" },
            ],
            Controls =
            [
                new StudioCompositionControl { Id = "road-filter", Kind = "filterSelect", SourceId = "shared-source" },
            ],
        };

        body = StudioCompositionBodyEditor.RemoveLayer(body, "roads");

        Assert.Equal(["roads-labels"], body.Layers.Select(layer => layer.Id));
        Assert.Equal("shared-source", Assert.Single(body.Controls!).SourceId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SetLayerStyleRef_RebindsOneOwnerWithoutLeavingItsOldSharedMapping(bool shared)
    {
        using var style = JsonDocument.Parse("""
            {"version":8,"sources":{"source":{"type":"geojson","data":"/source"}},"layers":[
             {"id":"parcels","type":"fill","source":"source"},{"id":"roads","type":"line","source":"source"}]}
            """);
        using var reference = JsonDocument.Parse("""
            {"styleId":"old","layerBindings":[
             {"styleLayerId":"catalog-fill","targetLayerId":"parcels","layerType":"fill"},
             {"styleLayerId":"catalog-line","targetLayerId":"roads","layerType":"line"}]}
            """);
        var body = StudioCompositionBody.Empty with
        {
            MapSpec = style.RootElement.Clone(),
            StyleRefs = [reference.RootElement.Clone()],
            Layers = [new StudioCompositionLayer { Id = "parcels", SourceId = "source", StyleRef = "old" },
                new StudioCompositionLayer { Id = "roads", SourceId = "source", StyleRef = shared ? "old" : null }]
        };
        var changed = StudioCompositionBodyEditor.SetLayerStyleRef(body, "parcels", "new");
        Assert.Equal("new", changed.Layers[0].StyleRef);
        if (shared)
        {
            var mapping = Assert.Single(Assert.Single(changed.StyleRefs!).GetProperty("layerBindings").EnumerateArray());
            Assert.Equal("roads", mapping.GetProperty("targetLayerId").GetString());
        }
        else Assert.Empty(changed.StyleRefs!);
        Assert.Equal(2, body.StyleRefs![0].GetProperty("layerBindings").GetArrayLength());
    }

    [UnitTest]
    public void SetLayerStyleRef_WithMissingId_ThrowsNotFound()
        => Assert.Throws<StudioCompositionNotFoundException>(
            () => StudioCompositionBodyEditor.SetLayerStyleRef(StudioCompositionBody.Empty, "no-such-layer", "style_x"));

    [UnitTest]
    public void SetLayerStyleRef_WithNullStyleRef_ClearsTheBinding()
    {
        var body = StudioCompositionBodyEditor.AddLayer(
            StudioCompositionBody.Empty, new StudioCompositionLayer { Id = "parcels", StyleRef = "style_a" });

        body = StudioCompositionBodyEditor.SetLayerStyleRef(body, "parcels", styleRef: null);

        Assert.Null(body.Layers[0].StyleRef);
    }

    [UnitTest]
    public void SetLayerVisibility_WithMissingId_ThrowsNotFound()
        => Assert.Throws<StudioCompositionNotFoundException>(
            () => StudioCompositionBodyEditor.SetLayerVisibility(StudioCompositionBody.Empty, "no-such-layer", visible: false));

    [UnitTest]
    public void SetLayerVisibility_TogglesOnlyTheNamedLayer()
    {
        // honua-server#3199: before this seam AddLayer was the only writer of `visible`, and it
        // rejects duplicate ids -- so composition state had no way to change a layer's visibility.
        var body = StudioCompositionBodyEditor.AddLayer(
            StudioCompositionBody.Empty, new StudioCompositionLayer { Id = "parcels" });
        body = StudioCompositionBodyEditor.AddLayer(body, new StudioCompositionLayer { Id = "zoning" });

        body = StudioCompositionBodyEditor.SetLayerVisibility(body, "parcels", visible: false);

        Assert.False(body.Layers[0].Visible);
        Assert.True(body.Layers[1].Visible);

        // Re-showing restores it: the toggle is a plain set, not a one-way latch.
        body = StudioCompositionBodyEditor.SetLayerVisibility(body, "parcels", visible: true);
        Assert.True(body.Layers[0].Visible);
    }

    [UnitTest]
    public void SetLayerVisibility_PreservesTheLayersOtherMembers()
    {
        var body = StudioCompositionBodyEditor.AddLayer(
            StudioCompositionBody.Empty,
            new StudioCompositionLayer
            {
                Id = "parcels",
                SourceId = "content.parcels",
                Type = "fill",
                Title = "Parcels",
                StyleRef = "style_parcels_default",
            });

        body = StudioCompositionBodyEditor.SetLayerVisibility(body, "parcels", visible: false);

        var layer = body.Layers[0];
        Assert.False(layer.Visible);
        Assert.Equal("content.parcels", layer.SourceId);
        Assert.Equal("fill", layer.Type);
        Assert.Equal("Parcels", layer.Title);
        Assert.Equal("style_parcels_default", layer.StyleRef);
    }

    [UnitTest]
    public void SetView_ReplacesAnyExistingView()
    {
        var body = StudioCompositionBodyEditor.SetView(StudioCompositionBody.Empty, new StudioCompositionView { Zoom = 5 });
        body = StudioCompositionBodyEditor.SetView(body, new StudioCompositionView { Zoom = 12, Crs = "EPSG:4326" });

        Assert.Equal(12, body.View!.Zoom);
        Assert.Equal("EPSG:4326", body.View.Crs);
    }

    [UnitTest]
    public void AddWidget_WithDuplicateId_ThrowsConflict()
    {
        var body = StudioCompositionBodyEditor.AddWidget(
            StudioCompositionBody.Empty, new StudioCompositionWidget { Id = "legend", Kind = "legend" });

        Assert.Throws<StudioCompositionConflictException>(
            () => StudioCompositionBodyEditor.AddWidget(body, new StudioCompositionWidget { Id = "legend", Kind = "legend" }));
    }

    [UnitTest]
    public void RemoveWidget_WithMissingId_ThrowsNotFound()
        => Assert.Throws<StudioCompositionNotFoundException>(
            () => StudioCompositionBodyEditor.RemoveWidget(StudioCompositionBody.Empty, "no-such-widget"));

    [UnitTest]
    public void RemoveWidget_WithExistingId_RemovesOnlyThatWidget()
    {
        var body = StudioCompositionBody.Empty;
        body = StudioCompositionBodyEditor.AddWidget(body, new StudioCompositionWidget { Id = "legend", Kind = "legend" });
        body = StudioCompositionBodyEditor.AddWidget(body, new StudioCompositionWidget { Id = "table", Kind = "table" });

        body = StudioCompositionBodyEditor.RemoveWidget(body, "legend");

        Assert.Equal(["table"], body.Widgets.Select(w => w.Id));
    }

    [UnitTest]
    public void RemoveWidget_WhenLayoutReferencesWidget_ThrowsConflict()
    {
        var body = StudioCompositionBodyEditor.AddWidget(
            StudioCompositionBody.Empty, new StudioCompositionWidget { Id = "legend", Kind = "legend" });
        body = body with
        {
            Layout = new StudioLayout
            {
                Items = [new StudioLayoutItem { Ref = "widget:legend", X = 0, Y = 0, W = 1, H = 1 }]
            }
        };

        Assert.Throws<StudioCompositionConflictException>(
            () => StudioCompositionBodyEditor.RemoveWidget(body, "legend"));
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(StudioPackageFamily.Query)]
    [InlineData(StudioPackageFamily.Report)]
    [InlineData(StudioPackageFamily.Form)]
    [InlineData(StudioPackageFamily.Workflow)]
    [InlineData(StudioPackageFamily.Geoprocessing)]
    [InlineData(StudioPackageFamily.Etl)]
    [InlineData(StudioPackageFamily.Analysis)]
    public void EnsureCompositionEligibleFamily_RejectsNonCompositionFamilies(StudioPackageFamily family)
        => Assert.Throws<StudioCompositionFamilyException>(
            () => StudioCompositionBodyEditor.EnsureCompositionEligibleFamily(family));

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(StudioPackageFamily.Map)]
    [InlineData(StudioPackageFamily.App)]
    [InlineData(StudioPackageFamily.Dashboard)]
    public void EnsureCompositionEligibleFamily_AllowsMapAppAndDashboard(StudioPackageFamily family)
        => StudioCompositionBodyEditor.EnsureCompositionEligibleFamily(family);

    [UnitTest]
    public void InlineStyle_RoundTripPreservesOtherStylesAndUnrelatedMapMetadata()
    {
        using var source = JsonDocument.Parse("""
            {"layers":[{"id":"parcels","styleRef":"shared"},{"id":"roads","styleRef":"shared"}],
             "styleRefs":[{"styleId":"shared","license":"retained","body":{"roads":{"paint":{"line-color":"red"}}}}],
             "mapPackageId":"original","mapSpec":{"version":8,"sources":{},"layers":[]}}
            """);
        using var overrides = JsonDocument.Parse("""
            {"parcels":{"paint":{"fill-color":["match",["get","zoning"],"R","green","gray"]}},
             "parcels__outline":{"paint":{"line-width":2}}}
            """);
        var envelope = BuildEnvelope(StudioPackageFamily.Map, source.RootElement.Clone());
        var before = StudioCompositionBodyEditor.ReadBody(envelope);
        var changed = StudioCompositionBodyEditor.SetInlineLayerStyle(before, "parcels", overrides.RootElement);
        var written = StudioCompositionBodyEditor.WriteBody(envelope, changed);
        var reopened = StudioCompositionBodyEditor.ReadBody(written);

        Assert.Equal("honua-inline:parcels", reopened.Layers[0].StyleRef);
        Assert.Equal("shared", reopened.Layers[1].StyleRef);
        Assert.Equal("shared", before.Layers[0].StyleRef);
        Assert.Equal("original", written.Body!.Value.GetProperty("mapPackageId").GetString());
        Assert.Equal(8, written.Body.Value.GetProperty("mapSpec").GetProperty("version").GetInt32());
        Assert.Equal(2, reopened.StyleRefs!.Count);
        Assert.Equal("retained", reopened.StyleRefs[0].GetProperty("license").GetString());
        Assert.Equal("match", reopened.StyleRefs[1].GetProperty("body").GetProperty("parcels")
            .GetProperty("paint").GetProperty("fill-color")[0].GetString());
    }

    [UnitTest]
    public void InlineStyle_RepeatedEditReplacesLocalBodyWithoutAccumulatingReferences()
    {
        using var first = JsonDocument.Parse("""{"parcels":{"paint":{"fill-color":"green"}}}""");
        using var second = JsonDocument.Parse("""{"parcels":{"paint":{"fill-color":"blue"}}}""");
        var body = StudioCompositionBodyEditor.AddLayer(StudioCompositionBody.Empty,
            new StudioCompositionLayer { Id = "parcels" });
        body = StudioCompositionBodyEditor.SetInlineLayerStyle(body, "parcels", first.RootElement);
        body = StudioCompositionBodyEditor.SetInlineLayerStyle(body, "parcels", second.RootElement);

        Assert.Equal("blue", Assert.Single(body.StyleRefs!).GetProperty("body").GetProperty("parcels")
            .GetProperty("paint").GetProperty("fill-color").GetString());
    }

    [Theory]
    [InlineData("{\"roads\":{\"paint\":{}}}")]
    [InlineData("{\"parcels\":{\"source\":\"private-source\"}}")]
    [InlineData("{\"parcels\":{\"id\":\"roads\"}}")]
    [InlineData("{\"parcels\":{\"minzoom\":\"invalid\"}}")]
    [InlineData("{\"parcels\":{\"minzoom\":12,\"maxzoom\":5}}")]
    [InlineData("{\"parcels\":{\"layout\":null}}")]
    [InlineData("{\"parcels\":{},\"parcels\":{}}")]
    [InlineData("{}")]
    public void InlineStyle_RefusesOtherTargetsAndMalformedOverrides(string json)
    {
        using var candidate = JsonDocument.Parse(json);
        var body = StudioCompositionBodyEditor.AddLayer(StudioCompositionBody.Empty,
            new StudioCompositionLayer { Id = "parcels" });

        Assert.Throws<StudioCompositionBodyException>(() =>
            StudioCompositionBodyEditor.SetInlineLayerStyle(body, "parcels", candidate.RootElement));
        Assert.Null(body.Layers[0].StyleRef);
    }

    [UnitTest]
    public void InlineStyle_RefusesAnOutlineIdOwnedByAnotherCompositionLayer()
    {
        using var candidate = JsonDocument.Parse("""{"parcels__outline":{"paint":{}}}""");
        var body = StudioCompositionBody.Empty with
        {
            Layers = [new StudioCompositionLayer { Id = "parcels" }, new StudioCompositionLayer { Id = "parcels__outline" }]
        };

        Assert.Throws<StudioCompositionBodyException>(() =>
            StudioCompositionBodyEditor.SetInlineLayerStyle(body, "parcels", candidate.RootElement));
    }

    [UnitTest]
    public void FullMapStyle_KeepsSourcesAndDetachesSharedAppearanceIntoLocalSnapshot()
    {
        using var original = JsonDocument.Parse("""
            {"version":8,"sources":{"parcels":{"type":"geojson","data":"/authorized/parcels"}},
             "layers":[{"id":"parcels","type":"fill","source":"parcels"}]}
            """);
        using var candidate = JsonDocument.Parse("""
            {"version":8,"sources":{"parcels":{"type":"geojson","data":"/authorized/parcels"}},
             "layers":[{"id":"parcels","type":"fill","source":"parcels","paint":{"fill-color":"green"}},
                       {"id":"parcel-labels","type":"symbol","source":"parcels","metadata":{"honua:compositionLayerId":"parcels"},"layout":{"text-field":["get","name"]}}]}
            """);
        var body = StudioCompositionBody.Empty with
        {
            MapSpec = original.RootElement.Clone(),
            Layers = [new StudioCompositionLayer { Id = "parcels", SourceId = "parcels", StyleRef = "shared" }]
        };
        var changed = StudioCompositionBodyEditor.SetMapStyle(body, candidate.RootElement);

        Assert.Equal("shared", body.Layers[0].StyleRef);
        Assert.Null(changed.Layers[0].StyleRef);
        Assert.Equal("parcels", changed.Layers[0].SourceId);
        Assert.Empty(changed.StyleRefs!);
        Assert.Equal(2, changed.MapSpec!.Value.GetProperty("layers").GetArrayLength());
    }

    [UnitTest]
    public void FullMapStyle_RejectsReassigningExistingLabelToAnotherAuthorizedOwner()
    {
        using var original = JsonDocument.Parse("""
            {"version":8,"sources":{"shared":{"type":"geojson","data":"/source"}},"layers":[
              {"id":"labels","type":"symbol","source":"shared","metadata":{"honua:compositionLayerId":"parcels"}}]}
            """);
        using var candidate = JsonDocument.Parse("""
            {"version":8,"sources":{"shared":{"type":"geojson","data":"/source"}},"layers":[
              {"id":"labels","type":"symbol","source":"shared","metadata":{"honua:compositionLayerId":"roads"}}]}
            """);
        var body = StudioCompositionBody.Empty with
        {
            MapSpec = original.RootElement.Clone(),
            Layers = [new StudioCompositionLayer { Id = "parcels", SourceId = "shared" },
                new StudioCompositionLayer { Id = "roads", SourceId = "shared" }]
        };
        Assert.Throws<StudioCompositionBodyException>(() =>
            StudioCompositionBodyEditor.SetMapStyle(body, candidate.RootElement));
    }

    [UnitTest]
    public void RemoveLayer_RemovesItsLabelsAndInlineStyle_WithoutDeletingOtherOwnersOnSameSource()
    {
        using var style = JsonDocument.Parse("""
            {"version":8,"sources":{"shared":{"type":"geojson","data":"/source"}},"layers":[
              {"id":"parcels","type":"fill","source":"shared"},
              {"id":"parcels__outline","type":"line","source":"shared"},
              {"id":"labels","type":"symbol","source":"shared","metadata":{"honua:compositionLayerId":"parcels"}},
              {"id":"other","type":"fill","source":"shared","metadata":{"honua:compositionLayerId":"other"}}]}
            """);
        using var reference = JsonDocument.Parse("""{"styleId":"honua-inline:parcels","body":{}}""");
        var body = StudioCompositionBody.Empty with
        {
            MapSpec = style.RootElement.Clone(),
            StyleRefs = [reference.RootElement.Clone()],
            Layers = [new StudioCompositionLayer { Id = "parcels", SourceId = "shared" },
                new StudioCompositionLayer { Id = "other", SourceId = "shared" }]
        };
        var changed = StudioCompositionBodyEditor.RemoveLayer(body, "parcels");
        Assert.Equal("other", Assert.Single(changed.Layers).Id);
        Assert.Equal("other", Assert.Single(changed.MapSpec!.Value.GetProperty("layers").EnumerateArray()).GetProperty("id").GetString());
        Assert.Empty(changed.StyleRefs!);
        Assert.Equal(4, body.MapSpec!.Value.GetProperty("layers").GetArrayLength());
    }

    [UnitTest]
    public void ReconcileMapStyle_AddsOnlyNewLayer_WithoutResurrectingDeletedAppearance()
    {
        using var original = JsonDocument.Parse("""
            {"version":8,"sources":{"parcels":{"type":"geojson","data":"/parcels"}},
             "layers":[{"id":"background","type":"background"}]}
            """);
        using var projection = JsonDocument.Parse("""
            {"version":8,"sources":{"parcels":{"type":"geojson","data":"/parcels"},"roads":{"type":"geojson","data":"/roads"}},
             "layers":[{"id":"parcels","type":"fill","source":"parcels"},
                       {"id":"roads","type":"line","source":"roads","paint":{"line-color":"blue"}}]}
            """);
        var before = StudioCompositionBody.Empty with
        {
            MapSpec = original.RootElement.Clone(),
            Layers = [new StudioCompositionLayer { Id = "parcels", SourceId = "parcels" }]
        };
        var added = StudioCompositionBodyEditor.AddLayer(before,
            new StudioCompositionLayer { Id = "roads", SourceId = "roads" }, "parcels");
        var after = StudioCompositionBodyEditor.ReconcileMapStyle(before, added, projection.RootElement);
        Assert.Equal(new[] { "background", "roads" }, after.MapSpec!.Value.GetProperty("layers")
            .EnumerateArray().Select(layer => layer.GetProperty("id").GetString()));
        Assert.Equal(2, after.MapSpec.Value.GetProperty("sources").EnumerateObject().Count());

        var removed = StudioCompositionBodyEditor.RemoveLayer(after, "roads");
        var restored = StudioCompositionBodyEditor.ReconcileMapStyle(after, removed, original.RootElement);
        Assert.Equal("background", Assert.Single(restored.MapSpec!.Value.GetProperty("layers").EnumerateArray())
            .GetProperty("id").GetString());
        Assert.False(restored.MapSpec.Value.GetProperty("sources").TryGetProperty("roads", out _));
    }

    [UnitTest]
    public void ReconcileMapStyle_ReordersOwnedRendersOnlyWhenCompositionOrderChanges()
    {
        using var style = JsonDocument.Parse("""
            {"version":8,"sources":{"shared":{"type":"geojson","data":"/source"}},"layers":[
              {"id":"background","type":"background"},
              {"id":"parcels","type":"fill","source":"shared"},
              {"id":"roads","type":"line","source":"shared"},
              {"id":"labels","type":"symbol","source":"shared","metadata":{"honua:compositionLayerId":"parcels"}}]}
            """);
        var before = StudioCompositionBody.Empty with
        {
            MapSpec = style.RootElement.Clone(),
            Layers = [new StudioCompositionLayer { Id = "parcels", SourceId = "shared" },
                new StudioCompositionLayer { Id = "roads", SourceId = "shared" }]
        };
        var unchanged = StudioCompositionBodyEditor.ReconcileMapStyle(before, before, style.RootElement);
        Assert.True(JsonElement.DeepEquals(style.RootElement, unchanged.MapSpec!.Value));

        var reordered = StudioCompositionBodyEditor.ReconcileMapStyle(before,
            before with { Layers = [before.Layers[1], before.Layers[0]] }, style.RootElement);
        Assert.Equal(new[] { "background", "roads", "parcels", "labels" }, reordered.MapSpec!.Value
            .GetProperty("layers").EnumerateArray().Select(layer => layer.GetProperty("id").GetString()));
        Assert.Equal("parcels", before.MapSpec!.Value.GetProperty("layers")[1].GetProperty("id").GetString());
    }

    [UnitTest]
    public void ReconcileMapStyle_GenericLayerRemovalCleansOwnedAppearanceAndInlineReference()
    {
        using var style = JsonDocument.Parse("""
            {"version":8,"sources":{"shared":{"type":"geojson","data":"/source"}},"layers":[
              {"id":"parcels","type":"fill","source":"shared"},
              {"id":"labels","type":"symbol","source":"shared","metadata":{"honua:compositionLayerId":"parcels"}},
              {"id":"roads","type":"line","source":"shared"}]}
            """);
        using var removedReference = JsonDocument.Parse("""{"styleId":"honua-inline:parcels","body":{}}""");
        using var retainedReference = JsonDocument.Parse("""{"styleId":"shared-roads"}""");
        var before = StudioCompositionBody.Empty with
        {
            MapSpec = style.RootElement.Clone(),
            StyleRefs = [removedReference.RootElement.Clone(), retainedReference.RootElement.Clone()],
            Layers = [new StudioCompositionLayer { Id = "parcels", SourceId = "shared" },
                new StudioCompositionLayer { Id = "roads", SourceId = "shared", StyleRef = "shared-roads" }]
        };
        // A visual editor sends the replacement composition without calling Core.RemoveLayer.
        var changed = StudioCompositionBodyEditor.ReconcileMapStyle(before,
            before with { Layers = [before.Layers[1]] }, style.RootElement);
        Assert.Equal("roads", Assert.Single(changed.MapSpec!.Value.GetProperty("layers").EnumerateArray())
            .GetProperty("id").GetString());
        Assert.Equal("shared-roads", Assert.Single(changed.StyleRefs!).GetProperty("styleId").GetString());
        Assert.True(changed.MapSpec.Value.GetProperty("sources").TryGetProperty("shared", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Tier", "Fast")]
    public async Task PrepareStyleUpdate_PreservesSharedRefsForRemoval_ButDetachesExplicitAppearance(bool editAppearance)
    {
        using var style = JsonDocument.Parse("""
            {"version":8,"sources":{"shared":{"type":"geojson","data":"/source"}},"layers":[
              {"id":"parcels","type":"fill","source":"shared"},
              {"id":"roads","type":"line","source":"shared","paint":{"line-color":"blue"}}]}
            """);
        using var reference = JsonDocument.Parse("""{"styleId":"shared-roads"}""");
        var before = StudioCompositionBody.Empty with
        {
            MapSpec = style.RootElement.Clone(),
            StyleRefs = [reference.RootElement.Clone()],
            Layers = [new StudioCompositionLayer { Id = "parcels", SourceId = "shared" },
                new StudioCompositionLayer { Id = "roads", SourceId = "shared", StyleRef = "shared-roads" }]
        };
        var after = before with { Layers = [before.Layers[1]] };
        if (editAppearance)
        {
            using var edited = JsonDocument.Parse(style.RootElement.GetRawText().Replace("blue", "green", StringComparison.Ordinal));
            after = after with { MapSpec = edited.RootElement.Clone() };
        }
        var principal = new ClaimsPrincipal(new ClaimsIdentity([], "test"));
        var authorizer = Substitute.For<IStudioMapStyleSourceAuthorizer>();
        authorizer.AuthorizeAsync(principal, Arg.Any<StudioCompositionBody>(), CancellationToken.None)
            .Returns(after with { MapSpec = style.RootElement.Clone() });
        authorizer.AuthorizeStyleReferencesAsync(principal, Arg.Any<StudioCompositionBody>(), CancellationToken.None)
            .Returns(call => call.ArgAt<StudioCompositionBody>(1));
        var validator = Substitute.For<IMapStyleValidator>();
        validator.TryValidate(Arg.Any<JsonElement>(), out Arg.Any<string?>()).Returns(true);
        var result = await StudioMapStyleMutationPreparation.PrepareAsync(principal, before, after,
            authorizer, validator, CancellationToken.None);

        var rendered = Assert.Single(result.MapSpec!.Value.GetProperty("layers").EnumerateArray());
        Assert.Equal("roads", rendered.GetProperty("id").GetString());
        Assert.Equal(editAppearance ? "green" : "blue", rendered.GetProperty("paint").GetProperty("line-color").GetString());
        if (editAppearance)
        {
            Assert.Null(result.Layers[0].StyleRef);
            Assert.Empty(result.StyleRefs!);
        }
        else
        {
            Assert.Equal("shared-roads", result.Layers[0].StyleRef);
            Assert.Single(result.StyleRefs!);
        }
        await authorizer.Received(1).AuthorizeAsync(principal,
            Arg.Is<StudioCompositionBody>(body => body.MapSpec == null && body.Layers.Count == 1), CancellationToken.None);
    }

    [UnitTest]
    public async Task PrepareMapStyle_InitialBaseAppearancePreservesExplicitSharedSelection()
    {
        using var style = JsonDocument.Parse("""
            {"version":8,"sources":{"source":{"type":"geojson","data":"/source"}},"layers":[
             {"id":"parcels","type":"fill","source":"source","paint":{"fill-color":"green"}}]}
            """);
        using var reference = JsonDocument.Parse("""{"styleId":"shared"}""");
        var after = StudioCompositionBody.Empty with
        {
            MapSpec = style.RootElement.Clone(),
            StyleRefs = [reference.RootElement.Clone()],
            Layers = [new StudioCompositionLayer { Id = "parcels", SourceId = "source", StyleRef = "shared" }]
        };
        using var generated = JsonDocument.Parse(style.RootElement.GetRawText().Replace("green", "blue", StringComparison.Ordinal));
        var principal = new ClaimsPrincipal(new ClaimsIdentity([], "test"));
        var authorizer = Substitute.For<IStudioMapStyleSourceAuthorizer>();
        authorizer.AuthorizeAsync(principal, Arg.Any<StudioCompositionBody>(), CancellationToken.None)
            .Returns(after with { MapSpec = generated.RootElement.Clone() });
        authorizer.AuthorizeStyleReferencesAsync(principal, Arg.Any<StudioCompositionBody>(), CancellationToken.None)
            .Returns(call => call.ArgAt<StudioCompositionBody>(1));
        var validator = Substitute.For<IMapStyleValidator>();
        validator.TryValidate(Arg.Any<JsonElement>(), out Arg.Any<string?>()).Returns(true);
        var result = await StudioMapStyleMutationPreparation.PrepareAsync(principal, StudioCompositionBody.Empty,
            after, authorizer, validator, CancellationToken.None);
        Assert.Equal("shared", result.Layers[0].StyleRef);
        Assert.Single(result.StyleRefs!);
        Assert.Equal("green", result.MapSpec!.Value.GetProperty("layers")[0].GetProperty("paint").GetProperty("fill-color").GetString());
        await authorizer.Received(1).AuthorizeStyleReferencesAsync(principal,
            Arg.Is<StudioCompositionBody>(body => body.StyleRefs!.Count == 1), CancellationToken.None);
    }

    [UnitTest]
    public void ReconcileMapStyle_RejectsClientSourceRetargetingDuringLayerAdd()
    {
        using var original = JsonDocument.Parse("""
            {"version":8,"sources":{"parcels":{"type":"geojson","data":"/parcels"}},
             "layers":[{"id":"parcels","type":"fill","source":"parcels"}]}
            """);
        var before = StudioCompositionBody.Empty with
        {
            MapSpec = original.RootElement.Clone(),
            Layers = [new StudioCompositionLayer { Id = "parcels", SourceId = "parcels" }]
        };
        var candidate = System.Text.Json.Nodes.JsonNode.Parse(original.RootElement.GetRawText())!;
        candidate["sources"]!["parcels"]!["data"] = "https://unapproved.example/private";
        using var changed = JsonDocument.Parse(candidate.ToJsonString());
        var after = before with { MapSpec = changed.RootElement.Clone() };
        Assert.Throws<StudioCompositionBodyException>(() =>
            StudioCompositionBodyEditor.ReconcileMapStyle(before, after, original.RootElement));
    }

    [Theory]
    [InlineData("sources", "{\"parcels\":{\"type\":\"geojson\",\"data\":\"https://unapproved.example/data\"}}")]
    [InlineData("glyphs", "\"https://unapproved.example/fonts/{fontstack}/{range}.pbf\"")]
    [InlineData("sprite", "\"https://unapproved.example/sprite\"")]
    [InlineData("imports", "[{\"id\":\"foreign\",\"url\":\"https://unapproved.example/style\"}]")]
    [InlineData("layers", "[{\"id\":\"foreign\",\"type\":\"fill\",\"source\":\"private-source\"}]")]
    [InlineData("layers", "[{\"id\":\"foreign\",\"type\":\"fill\",\"source\":\"parcels\",\"source-layer\":\"private-table\"}]")]
    public void FullMapStyle_RejectsSourceAndExternalAssetChanges(string member, string replacement)
    {
        using var original = JsonDocument.Parse("""
            {"version":8,"sources":{"parcels":{"type":"geojson","data":"/authorized/parcels"}},
             "layers":[{"id":"parcels","type":"fill","source":"parcels"}]}
            """);
        var candidate = System.Text.Json.Nodes.JsonNode.Parse(original.RootElement.GetRawText())!;
        candidate[member] = System.Text.Json.Nodes.JsonNode.Parse(replacement);
        using var candidateDocument = JsonDocument.Parse(candidate.ToJsonString());
        var body = StudioCompositionBody.Empty with { MapSpec = original.RootElement.Clone() };

        Assert.Throws<StudioCompositionBodyException>(() =>
            StudioCompositionBodyEditor.SetMapStyle(body, candidateDocument.RootElement));
    }

    private static StudioPackageEnvelope BuildEnvelope(StudioPackageFamily family, System.Text.Json.JsonElement? body)
        => new()
        {
            Family = family,
            SchemaVersion = "1.0",
            Body = body,
        };
}
