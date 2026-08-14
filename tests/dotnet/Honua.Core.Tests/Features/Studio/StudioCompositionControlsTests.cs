// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Studio;

/// <summary>
/// Unit tests for the <c>controls</c> composition collection (geospatial-mcp ADR-0031):
/// the <see cref="StudioCompositionBodyEditor"/> add/remove operations and their admission
/// gate, the closed control-kind vocabulary, the cascade-or-reject removal obligation, and
/// the <c>WriteBody</c> round trip that must keep an authored controls block alive across
/// unrelated composition mutations. Mirrors
/// <see cref="StudioCompositionInteractionsTests"/> one collection over.
/// </summary>
public sealed class StudioCompositionControlsTests
{
    [UnitTest]
    public void AddControl_OnACompositionWithNoControls_AddsTheControl()
    {
        var body = StudioCompositionBodyEditor.AddControl(ParcelComposition(), YearSlider());

        var control = Assert.Single(body.Controls!);
        Assert.Equal("year-slider", control.Id);
        Assert.Equal("timeSlider", control.Kind);
        Assert.Equal("Year built", control.Title);
        Assert.Equal("parcels", control.SourceId);
        Assert.Equal("yearBuilt", control.Config!.Value.GetProperty("field").GetString());
    }

    [UnitTest]
    public void AddControl_WithSameId_ReplacesInPlaceRatherThanAppending()
    {
        var body = StudioCompositionBodyEditor.AddControl(ParcelComposition(), YearSlider());
        body = StudioCompositionBodyEditor.AddControl(body, Control("basemap-picker", "basemapSwitcher"));

        body = StudioCompositionBodyEditor.AddControl(body, YearSlider() with { Kind = "filterSlider" });

        Assert.Equal(2, body.Controls!.Count);
        Assert.Equal("year-slider", body.Controls[0].Id);
        Assert.Equal("filterSlider", body.Controls[0].Kind);
        Assert.Equal("basemap-picker", body.Controls[1].Id);
    }

    [Theory]
    [InlineData("navigation")]
    [InlineData("scale")]
    [InlineData("fullscreen")]
    [InlineData("geolocate")]
    [InlineData("search")]
    [InlineData("measure")]
    [InlineData("timeSlider")]
    [InlineData("filterSelect")]
    [InlineData("filterSlider")]
    [InlineData("filterDateRange")]
    [InlineData("bookmarks")]
    [InlineData("opacity")]
    [InlineData("attribution")]
    [InlineData("basemapSwitcher")]
    public void AddControl_AcceptsEveryKindOfTheClosedVocabulary(string kind)
    {
        var body = StudioCompositionBodyEditor.AddControl(ParcelComposition(), Control("c1", kind));

        Assert.Equal(kind, Assert.Single(body.Controls!).Kind);
    }

    [Theory]
    [InlineData("draw")]
    [InlineData("edit")]
    [InlineData("legend")]
    [InlineData("")]
    [InlineData(null)]
    public void AddControl_OutsideTheClosedKindVocabulary_IsRejected(string? kind)
    {
        // `draw`/`edit` are the load-bearing cases: ADR-0031 deliberately admits no
        // feature-editing control, so source-record mutation cannot become a side door
        // around the governed edit_features boundary (ADR-0028).
        var error = Assert.Throws<StudioCompositionConflictException>(() =>
            StudioCompositionBodyEditor.AddControl(ParcelComposition(), Control("c1", kind!)));

        Assert.Contains("must be one of", error.Message, StringComparison.Ordinal);
    }

    [UnitTest]
    public void AddControl_WithBlankOrOversizedId_IsRejected()
    {
        var blank = Assert.Throws<StudioCompositionConflictException>(() =>
            StudioCompositionBodyEditor.AddControl(ParcelComposition(), Control(" ", "navigation")));
        Assert.Contains("non-empty 'id'", blank.Message, StringComparison.Ordinal);

        var oversized = Assert.Throws<StudioCompositionConflictException>(() =>
            StudioCompositionBodyEditor.AddControl(
                ParcelComposition(),
                Control(new string('c', StudioInteractionVocabulary.MaxControlIdLength + 1), "navigation")));
        Assert.Contains("characters or fewer", oversized.Message, StringComparison.Ordinal);
    }

    [UnitTest]
    public void AddControl_WithOversizedTitle_IsRejected()
    {
        var error = Assert.Throws<StudioCompositionConflictException>(() =>
            StudioCompositionBodyEditor.AddControl(
                ParcelComposition(),
                Control("navigation", "navigation") with
                {
                    Title = new string('t', StudioInteractionVocabulary.MaxControlTitleLength + 1),
                }));

        Assert.Contains("title", error.Message, StringComparison.Ordinal);
        Assert.Contains("characters or fewer", error.Message, StringComparison.Ordinal);
    }

    [UnitTest]
    public void AddControl_WithOversizedSourceId_IsRejected()
    {
        var sourceId = new string('s', StudioInteractionVocabulary.MaxControlSourceIdLength + 1);
        var body = ParcelComposition() with
        {
            Layers = [new StudioCompositionLayer { Id = sourceId }],
        };

        var error = Assert.Throws<StudioCompositionConflictException>(() =>
            StudioCompositionBodyEditor.AddControl(
                body,
                new StudioCompositionControl { Id = "filter", Kind = "filterSelect", SourceId = sourceId }));

        Assert.Contains("sourceId", error.Message, StringComparison.Ordinal);
        Assert.Contains("characters or fewer", error.Message, StringComparison.Ordinal);
    }

    [UnitTest]
    public void AddControl_WithAnUnresolvableSourceId_IsRejected()
    {
        // ADR-0031 makes source resolution a validation-gate responsibility. A misspelled
        // sourceId ('parcel' for layer 'parcels') would otherwise persist an affordance
        // whose domain no host can populate, failing at render time instead of authoring
        // time — and the vendored add_control schema advertises that we check it.
        var error = Assert.Throws<StudioCompositionConflictException>(() =>
            StudioCompositionBodyEditor.AddControl(
                ParcelComposition(),
                new StudioCompositionControl { Id = "f", Kind = "filterSelect", SourceId = "parcel" }));

        Assert.Contains("does not resolve", error.Message, StringComparison.Ordinal);
        Assert.Contains("parcel", error.Message, StringComparison.Ordinal);
    }

    [UnitTest]
    public void AddControl_ResolvesSourceIdAgainstLayerIdsAndTheirDatasources()
    {
        // The document declares its data surface through layers only, so a control source
        // legitimately names either the layer or the datasource that layer binds.
        var body = StudioCompositionBody.Empty with
        {
            Layers = [new StudioCompositionLayer { Id = "parcels", SourceId = "ds-parcels" }],
        };

        var byLayerId = StudioCompositionBodyEditor.AddControl(
            body, new StudioCompositionControl { Id = "a", Kind = "filterSelect", SourceId = "parcels" });
        Assert.Equal("parcels", Assert.Single(byLayerId.Controls!).SourceId);

        var byDatasource = StudioCompositionBodyEditor.AddControl(
            body, new StudioCompositionControl { Id = "b", Kind = "filterSelect", SourceId = "ds-parcels" });
        Assert.Equal("ds-parcels", Assert.Single(byDatasource.Controls!).SourceId);
    }

    [UnitTest]
    public void AddControl_ResolvesSourceIdAgainstCanonicalSourceBindingsWithoutALayer()
    {
        using var stored = JsonDocument.Parse(
            """
            {
              "sourceBindings": [
                {
                  "sourceId": "ds-parcels",
                  "protocol": "ogc_api_features",
                  "locator": { "url": "https://example.test/collections/parcels" }
                }
              ],
              "layers": []
            }
            """);
        var body = StudioCompositionBodyEditor.ReadBody(
            BuildEnvelope(StudioPackageFamily.Map, stored.RootElement.Clone()));

        var updated = StudioCompositionBodyEditor.AddControl(
            body,
            new StudioCompositionControl { Id = "filter", Kind = "filterSelect", SourceId = "ds-parcels" });

        Assert.Equal("ds-parcels", Assert.Single(updated.Controls!).SourceId);
    }

    [UnitTest]
    public void AddControl_WithoutASourceId_IsAcceptedForPresentationOnlyKinds()
    {
        // navigation/scale/fullscreen/attribution render no dataset, so omitting sourceId
        // must stay legal — the resolution rule applies only when one is supplied.
        var body = StudioCompositionBodyEditor.AddControl(ParcelComposition(), Control("nav", "navigation"));

        Assert.Null(Assert.Single(body.Controls!).SourceId);
    }

    [UnitTest]
    public void AddControl_WithABlankSourceId_IsRejected()
    {
        var error = Assert.Throws<StudioCompositionConflictException>(() =>
            StudioCompositionBodyEditor.AddControl(
                ParcelComposition(),
                new StudioCompositionControl { Id = "f", Kind = "filterSelect", SourceId = "   " }));

        Assert.Contains("does not resolve", error.Message, StringComparison.Ordinal);
    }

    [UnitTest]
    public void RemoveControl_WithUnknownId_Throws_AndIsNotANoOp()
    {
        var body = StudioCompositionBodyEditor.AddControl(ParcelComposition(), YearSlider());

        Assert.Throws<StudioCompositionNotFoundException>(
            () => StudioCompositionBodyEditor.RemoveControl(body, "never-added"));
    }

    [UnitTest]
    public void RemoveControl_WithExistingId_RemovesOnlyThatControl()
    {
        var body = StudioCompositionBodyEditor.AddControl(ParcelComposition(), YearSlider());
        body = StudioCompositionBodyEditor.AddControl(body, Control("basemap-picker", "basemapSwitcher"));

        body = StudioCompositionBodyEditor.RemoveControl(body, "year-slider");

        Assert.Equal(["basemap-picker"], body.Controls!.Select(c => c.Id));
    }

    [UnitTest]
    public void RemoveControl_WithoutCascade_RejectsRemovalWhileAnInteractionStillReferencesIt()
    {
        // The dangling-binding rule: silently keeping an unresolvable control: binding is
        // NOT conformant, so the default is a rejection that names the way forward.
        var body = BoundComposition();

        var error = Assert.Throws<StudioCompositionConflictException>(
            () => StudioCompositionBodyEditor.RemoveControl(body, "year-slider"));

        Assert.Contains("year-filters-parcels", error.Message, StringComparison.Ordinal);
        Assert.Contains("cascadeInteractions=true", error.Message, StringComparison.Ordinal);

        // Rejected, not partially applied.
        Assert.Single(body.Controls!);
        Assert.Single(body.Interactions!);
    }

    [UnitTest]
    public void RemoveControl_WithCascade_RemovesTheControlAndItsReferencingInteractions()
    {
        var body = BoundComposition();
        // A second binding that does NOT touch the control must survive the cascade.
        body = StudioCompositionBodyEditor.BindInteraction(
            body,
            new StudioInteraction
            {
                Id = "hover-highlights",
                On = new StudioInteractionEvent { Ref = "layer:parcels", Event = "featureHover" },
                Do = new StudioInteractionAction { Ref = "widget:area-chart", Verb = "runWidgetQuery" },
            });

        body = StudioCompositionBodyEditor.RemoveControl(body, "year-slider", cascadeInteractions: true);

        Assert.Empty(body.Controls!);
        Assert.Equal(["hover-highlights"], body.Interactions!.Select(i => i.Id));
    }

    [UnitTest]
    public void RemoveControl_WithCascade_AlsoDropsBindingsThatOnlyTargetTheControl()
    {
        // The obligation covers do.ref as well as on.ref — either leaves a dangling ref.
        var body = StudioCompositionBodyEditor.AddControl(ParcelComposition(), YearSlider());
        body = StudioCompositionBodyEditor.BindInteraction(
            body,
            new StudioInteraction
            {
                Id = "select-sets-slider",
                On = new StudioInteractionEvent { Ref = "layer:parcels", Event = "featureSelect" },
                Do = new StudioInteractionAction { Ref = "control:year-slider", Verb = "setFilter" },
            });

        var rejected = Assert.Throws<StudioCompositionConflictException>(
            () => StudioCompositionBodyEditor.RemoveControl(body, "year-slider"));
        Assert.Contains("select-sets-slider", rejected.Message, StringComparison.Ordinal);

        body = StudioCompositionBodyEditor.RemoveControl(body, "year-slider", cascadeInteractions: true);
        Assert.Empty(body.Interactions!);
    }

    [UnitTest]
    public void WriteBody_PreservesAnAuthoredControlsBlock_AcrossUnrelatedCompositionMutations()
    {
        // Same projection-overlay regression the interactions block guards against: a
        // controls block authored via add_control must survive every later
        // add-layer/set-view/add-widget edit AND leave the document's unmodelled members
        // (mapPackageId, format, sourceBindings, ...) untouched.
        using var stored = JsonDocument.Parse(
            """
            {
              "mapPackageId": "map_maui_parcels",
              "format": "honua_map_package.v1",
              "sourceBindings": [{ "bindingId": "b1", "datasetId": "parcels" }],
              "layers": [{ "id": "parcels", "type": "fill" }],
              "widgets": [{ "id": "area-chart", "kind": "chart" }]
            }
            """);
        var envelope = BuildEnvelope(StudioPackageFamily.Map, stored.RootElement.Clone());

        envelope = StudioCompositionBodyEditor.WriteBody(
            envelope,
            StudioCompositionBodyEditor.AddControl(StudioCompositionBodyEditor.ReadBody(envelope), YearSlider()));

        envelope = Mutate(envelope, body => StudioCompositionBodyEditor.AddLayer(
            body, new StudioCompositionLayer { Id = "zoning", Type = "fill" }));
        envelope = Mutate(envelope, body => StudioCompositionBodyEditor.SetView(
            body, new StudioCompositionView { Zoom = 11 }));
        envelope = Mutate(envelope, body => StudioCompositionBodyEditor.AddWidget(
            body, new StudioCompositionWidget { Id = "legend", Kind = "legend" }));

        var final = StudioCompositionBodyEditor.ReadBody(envelope);
        var surviving = Assert.Single(final.Controls!);
        Assert.Equal("year-slider", surviving.Id);
        Assert.Equal("timeSlider", surviving.Kind);
        Assert.Equal("yearBuilt", surviving.Config!.Value.GetProperty("field").GetString());

        var body = envelope.Body!.Value;
        Assert.Equal("map_maui_parcels", body.GetProperty("mapPackageId").GetString());
        Assert.Equal(1, body.GetProperty("sourceBindings").GetArrayLength());
        Assert.Equal(["parcels", "zoning"], final.Layers.Select(l => l.Id));
        Assert.Equal(["area-chart", "legend"], final.Widgets.Select(w => w.Id));
        Assert.Equal(11, final.View!.Zoom);
    }

    [UnitTest]
    public void WriteBody_OnADocumentWithNoControls_DoesNotMaterializeAnEmptyBlock()
    {
        // Null (rather than empty) is what keeps every unrelated map/app package from
        // growing a `"controls": []` member on its first unrelated edit.
        using var stored = JsonDocument.Parse("""{"format":"honua_map_package.v1","layers":[]}""");
        var envelope = BuildEnvelope(StudioPackageFamily.Map, stored.RootElement.Clone());

        var written = StudioCompositionBodyEditor.WriteBody(
            envelope,
            StudioCompositionBodyEditor.AddLayer(
                StudioCompositionBodyEditor.ReadBody(envelope),
                new StudioCompositionLayer { Id = "parcels" }));

        Assert.False(written.Body!.Value.TryGetProperty("controls", out _));
    }

    [UnitTest]
    public void RemoveControl_OfTheLastControl_ClearsTheStoredBlock()
    {
        using var stored = JsonDocument.Parse(
            """
            {
              "layers": [{ "id": "parcels" }],
              "controls": [{ "id": "year-slider", "kind": "timeSlider", "sourceId": "parcels" }]
            }
            """);
        var envelope = BuildEnvelope(StudioPackageFamily.Map, stored.RootElement.Clone());

        envelope = Mutate(envelope, body => StudioCompositionBodyEditor.RemoveControl(body, "year-slider"));

        Assert.True(envelope.Body!.Value.TryGetProperty("controls", out var controls));
        Assert.Equal(0, controls.GetArrayLength());
        Assert.Empty(StudioCompositionBodyEditor.ReadBody(envelope).Controls!);
    }

    [UnitTest]
    public void ReadBody_WithAStoredControlsBlock_ProjectsEveryMember()
    {
        using var stored = JsonDocument.Parse(
            """
            {
              "layers": [{ "id": "parcels" }],
              "controls": [
                { "id": "year-slider", "kind": "timeSlider", "title": "Year built", "sourceId": "parcels", "config": { "step": 1 } },
                { "id": "nav", "kind": "navigation" }
              ]
            }
            """);

        var body = StudioCompositionBodyEditor.ReadBody(BuildEnvelope(StudioPackageFamily.Map, stored.RootElement.Clone()));

        Assert.Equal(2, body.Controls!.Count);
        Assert.Equal("Year built", body.Controls[0].Title);
        Assert.Equal(1, body.Controls[0].Config!.Value.GetProperty("step").GetInt32());
        Assert.Null(body.Controls[1].SourceId);
        Assert.Null(body.Controls[1].Config);
    }

    [UnitTest]
    public void ReadBody_WithANullControlEntry_IsRejected()
    {
        using var stored = JsonDocument.Parse("""{"layers":[],"controls":[null]}""");
        var envelope = BuildEnvelope(StudioPackageFamily.Map, stored.RootElement.Clone());

        Assert.Throws<StudioCompositionBodyException>(() => StudioCompositionBodyEditor.ReadBody(envelope));
    }

    [UnitTest]
    public void Vocabulary_ClosedControlKindSetAndBoundsMatchTheStandard()
    {
        Assert.Equal(
            [
                "navigation", "scale", "fullscreen", "geolocate", "search", "measure", "timeSlider",
                "filterSelect", "filterSlider", "filterDateRange", "bookmarks", "opacity", "attribution",
                "basemapSwitcher",
            ],
            StudioInteractionVocabulary.ControlKinds);
        Assert.DoesNotContain("draw", StudioInteractionVocabulary.ControlKinds);
        Assert.Equal(200, StudioInteractionVocabulary.MaxControlIdLength);
        Assert.Equal(200, StudioInteractionVocabulary.MaxControlTitleLength);
        Assert.Equal(200, StudioInteractionVocabulary.MaxControlSourceIdLength);
        Assert.True(StudioInteractionVocabulary.IsControlRef("control:year-slider"));
        Assert.False(StudioInteractionVocabulary.IsControlRef("control:"));
        Assert.False(StudioInteractionVocabulary.IsControlRef("widget:area-chart"));
    }

    private static StudioCompositionBody ParcelComposition() => StudioCompositionBody.Empty with
    {
        Layers = [new StudioCompositionLayer { Id = "parcels", Type = "fill" }],
        Widgets = [new StudioCompositionWidget { Id = "area-chart", Kind = "chart" }],
    };

    private static StudioCompositionBody BoundComposition()
    {
        var body = StudioCompositionBodyEditor.AddControl(ParcelComposition(), YearSlider());
        return StudioCompositionBodyEditor.BindInteraction(
            body,
            new StudioInteraction
            {
                Id = "year-filters-parcels",
                On = new StudioInteractionEvent { Ref = "control:year-slider", Event = "change" },
                Do = new StudioInteractionAction { Ref = "layer:parcels", Verb = "setFilter" },
            });
    }

    private static StudioCompositionControl YearSlider()
    {
        using var config = JsonDocument.Parse("""{"field":"yearBuilt"}""");
        return new StudioCompositionControl
        {
            Id = "year-slider",
            Kind = "timeSlider",
            Title = "Year built",
            SourceId = "parcels",
            Config = config.RootElement.Clone(),
        };
    }

    private static StudioCompositionControl Control(string id, string kind) => new() { Id = id, Kind = kind };

    private static StudioPackageEnvelope Mutate(
        StudioPackageEnvelope envelope,
        Func<StudioCompositionBody, StudioCompositionBody> mutate) =>
        StudioCompositionBodyEditor.WriteBody(envelope, mutate(StudioCompositionBodyEditor.ReadBody(envelope)));

    private static StudioPackageEnvelope BuildEnvelope(StudioPackageFamily family, JsonElement? body)
        => new()
        {
            Family = family,
            SchemaVersion = "1.0",
            Body = body,
        };
}
