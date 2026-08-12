// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Studio;

/// <summary>
/// Unit tests for the declarative <c>interactions</c>/<c>layout</c> composition blocks
/// (geospatial-mcp ADR-0030): the <see cref="StudioCompositionBodyEditor"/> bind/remove
/// operations and their admission gate, the shared
/// <see cref="StudioInteractionVocabulary"/> reference grammar, and the
/// <c>WriteBody</c> round trip that must keep an authored interactions block alive
/// across unrelated composition mutations.
/// </summary>
public sealed class StudioCompositionInteractionsTests
{
    [UnitTest]
    public void BindInteraction_OnEmptyComposition_AddsTheBinding()
    {
        var body = ParcelComposition();

        var bound = StudioCompositionBodyEditor.BindInteraction(body, SelectParcelFiltersChart());

        var interaction = Assert.Single(bound.Interactions!);
        Assert.Equal("select-parcel-filters-chart", interaction.Id);
        Assert.Equal("layer:parcels", interaction.On.Ref);
        Assert.Equal("featureSelect", interaction.On.Event);
        Assert.Equal("widget:area-chart", interaction.Do.Ref);
        Assert.Equal("setFilter", interaction.Do.Verb);
    }

    [UnitTest]
    public void BindInteraction_WithSameId_ReplacesInPlaceRatherThanAppending()
    {
        var body = ParcelComposition();
        body = StudioCompositionBodyEditor.BindInteraction(body, SelectParcelFiltersChart());
        body = StudioCompositionBodyEditor.BindInteraction(body, Interaction("hover-highlights", "featureHover"));

        // Re-bind the FIRST id with a different action verb.
        body = StudioCompositionBodyEditor.BindInteraction(
            body,
            SelectParcelFiltersChart() with
            {
                Do = new StudioInteractionAction { Ref = "map", Verb = "setViewport" },
            });

        Assert.Equal(2, body.Interactions!.Count);
        Assert.Equal("select-parcel-filters-chart", body.Interactions[0].Id);
        Assert.Equal("setViewport", body.Interactions[0].Do.Verb);
        Assert.Equal("hover-highlights", body.Interactions[1].Id);
    }

    [UnitTest]
    public void BindInteraction_WithUnknownEventOrVerb_IsRejected()
    {
        var body = ParcelComposition();

        Assert.Throws<StudioCompositionConflictException>(() => StudioCompositionBodyEditor.BindInteraction(
            body, SelectParcelFiltersChart() with { On = new StudioInteractionEvent { Ref = "layer:parcels", Event = "featureDoubleClick" } }));

        Assert.Throws<StudioCompositionConflictException>(() => StudioCompositionBodyEditor.BindInteraction(
            body, SelectParcelFiltersChart() with { Do = new StudioInteractionAction { Ref = "map", Verb = "setBasemap" } }));
    }

    [UnitTest]
    public void BindInteraction_WithOversizedId_IsRejected()
    {
        var interaction = SelectParcelFiltersChart() with
        {
            Id = new string('i', StudioInteractionVocabulary.MaxInteractionIdLength + 1),
        };

        var error = Assert.Throws<StudioCompositionConflictException>(() =>
            StudioCompositionBodyEditor.BindInteraction(ParcelComposition(), interaction));

        Assert.Contains("characters or fewer", error.Message, StringComparison.Ordinal);
    }

    [UnitTest]
    public void BindInteraction_WithUnresolvableRef_IsRejected()
    {
        var body = ParcelComposition();

        var unknownLayer = Assert.Throws<StudioCompositionConflictException>(() =>
            StudioCompositionBodyEditor.BindInteraction(
                body,
                SelectParcelFiltersChart() with { On = new StudioInteractionEvent { Ref = "layer:zoning", Event = "featureSelect" } }));
        Assert.Contains("does not resolve", unknownLayer.Message, StringComparison.Ordinal);

        var malformed = Assert.Throws<StudioCompositionConflictException>(() =>
            StudioCompositionBodyEditor.BindInteraction(
                body,
                SelectParcelFiltersChart() with { On = new StudioInteractionEvent { Ref = "parcels", Event = "featureSelect" } }));
        Assert.Contains("not a valid reference", malformed.Message, StringComparison.Ordinal);
    }

    [UnitTest]
    public void BindInteraction_WithControlRef_IsRejectedWithTheControlsMessage()
    {
        // Studio composition documents declare no controls collection, so a `control:`
        // reference is grammatical but can never resolve. The message has to say that
        // rather than the generic "does not resolve", or an agent will keep retrying.
        var body = ParcelComposition();

        var error = Assert.Throws<StudioCompositionConflictException>(() =>
            StudioCompositionBodyEditor.BindInteraction(
                body,
                SelectParcelFiltersChart() with
                {
                    On = new StudioInteractionEvent { Ref = "control:year-slider", Event = "change" },
                }));

        Assert.Contains("declare no controls collection", error.Message, StringComparison.Ordinal);
    }

    [UnitTest]
    public void BindInteraction_AtTheFanOutCap_RejectsTheNinthButAllowsRebindingTheEighth()
    {
        var body = ParcelComposition();
        for (var i = 0; i < StudioInteractionVocabulary.MaxInteractionsPerEventSource; i++)
        {
            body = StudioCompositionBodyEditor.BindInteraction(body, Interaction($"binding-{i}", "featureSelect"));
        }

        Assert.Equal(StudioInteractionVocabulary.MaxInteractionsPerEventSource, body.Interactions!.Count);

        var overCap = Assert.Throws<StudioCompositionConflictException>(() =>
            StudioCompositionBodyEditor.BindInteraction(body, Interaction("binding-8", "featureSelect")));
        Assert.Contains("may share the same event source", overCap.Message, StringComparison.Ordinal);

        // Replacing an existing binding does not grow the fan-out, so it stays legal.
        var rebound = StudioCompositionBodyEditor.BindInteraction(
            body,
            Interaction("binding-7", "featureSelect") with
            {
                Do = new StudioInteractionAction { Ref = "map", Verb = "setViewport" },
            });
        Assert.Equal(StudioInteractionVocabulary.MaxInteractionsPerEventSource, rebound.Interactions!.Count);

        // A DIFFERENT event source has its own budget.
        var otherSource = StudioCompositionBodyEditor.BindInteraction(body, Interaction("hover-1", "featureHover"));
        Assert.Equal(StudioInteractionVocabulary.MaxInteractionsPerEventSource + 1, otherSource.Interactions!.Count);
    }

    [UnitTest]
    public void RemoveInteraction_WithUnknownId_Throws_AndIsNotANoOp()
    {
        var body = StudioCompositionBodyEditor.BindInteraction(ParcelComposition(), SelectParcelFiltersChart());

        Assert.Throws<StudioCompositionNotFoundException>(
            () => StudioCompositionBodyEditor.RemoveInteraction(body, "never-bound"));
    }

    [UnitTest]
    public void RemoveInteraction_WithExistingId_RemovesOnlyThatBinding()
    {
        var body = ParcelComposition();
        body = StudioCompositionBodyEditor.BindInteraction(body, SelectParcelFiltersChart());
        body = StudioCompositionBodyEditor.BindInteraction(body, Interaction("hover-highlights", "featureHover"));

        body = StudioCompositionBodyEditor.RemoveInteraction(body, "select-parcel-filters-chart");

        Assert.Equal(["hover-highlights"], body.Interactions!.Select(i => i.Id));
    }

    [UnitTest]
    public void WriteBody_PreservesAnAuthoredInteractionsBlock_AcrossUnrelatedCompositionMutations()
    {
        // The regression this guards: StudioCompositionBody is a PROJECTION overlaid onto the
        // stored document. An interactions block authored via bind_interaction must survive
        // every later add-layer/set-view/add-widget edit AND leave the document's unmodelled
        // members (mapPackageId, format, sourceBindings, ...) untouched.
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

        var bound = StudioCompositionBodyEditor.BindInteraction(
            StudioCompositionBodyEditor.ReadBody(envelope), SelectParcelFiltersChart());
        envelope = StudioCompositionBodyEditor.WriteBody(envelope, bound);

        // Three unrelated composition mutations, each a full read→mutate→write round trip.
        envelope = Mutate(envelope, body => StudioCompositionBodyEditor.AddLayer(
            body, new StudioCompositionLayer { Id = "zoning", Type = "fill" }));
        envelope = Mutate(envelope, body => StudioCompositionBodyEditor.SetView(
            body, new StudioCompositionView { Zoom = 11 }));
        envelope = Mutate(envelope, body => StudioCompositionBodyEditor.AddWidget(
            body, new StudioCompositionWidget { Id = "legend", Kind = "legend" }));

        var final = StudioCompositionBodyEditor.ReadBody(envelope);
        var surviving = Assert.Single(final.Interactions!);
        Assert.Equal("select-parcel-filters-chart", surviving.Id);
        Assert.Equal("layer:parcels", surviving.On.Ref);
        Assert.Equal(
            "parcelId",
            surviving.Do.Args!.Value.GetProperty("field").GetString());

        // Unmodelled members are still there, and the ordinary edits landed.
        var body = envelope.Body!.Value;
        Assert.Equal("map_maui_parcels", body.GetProperty("mapPackageId").GetString());
        Assert.Equal("honua_map_package.v1", body.GetProperty("format").GetString());
        Assert.Equal(1, body.GetProperty("sourceBindings").GetArrayLength());
        Assert.Equal(["parcels", "zoning"], final.Layers.Select(l => l.Id));
        Assert.Equal(["area-chart", "legend"], final.Widgets.Select(w => w.Id));
        Assert.Equal(11, final.View!.Zoom);
    }

    [UnitTest]
    public void WriteBody_OnADocumentWithNoInteractions_DoesNotMaterializeAnEmptyBlock()
    {
        // Null (rather than empty) is what keeps every unrelated map/app package from
        // growing an `"interactions": []` member on its first edit.
        using var stored = JsonDocument.Parse("""{"format":"honua_map_package.v1","layers":[]}""");
        var envelope = BuildEnvelope(StudioPackageFamily.Map, stored.RootElement.Clone());

        var written = StudioCompositionBodyEditor.WriteBody(
            envelope,
            StudioCompositionBodyEditor.AddLayer(
                StudioCompositionBodyEditor.ReadBody(envelope),
                new StudioCompositionLayer { Id = "parcels" }));

        var writtenBody = written.Body!.Value;
        Assert.False(writtenBody.TryGetProperty("interactions", out _));
        Assert.False(writtenBody.TryGetProperty("layout", out _));
    }

    [UnitTest]
    public void RemoveInteraction_OfTheLastBinding_ClearsTheStoredBlock()
    {
        using var stored = JsonDocument.Parse(
            """
            {
              "layers": [{ "id": "parcels" }],
              "widgets": [{ "id": "area-chart", "kind": "chart" }],
              "interactions": [
                {
                  "id": "select-parcel-filters-chart",
                  "on": { "ref": "layer:parcels", "event": "featureSelect" },
                  "do": { "ref": "widget:area-chart", "verb": "setFilter" }
                }
              ]
            }
            """);
        var envelope = BuildEnvelope(StudioPackageFamily.Map, stored.RootElement.Clone());

        envelope = Mutate(envelope, body => StudioCompositionBodyEditor.RemoveInteraction(
            body, "select-parcel-filters-chart"));

        Assert.True(envelope.Body!.Value.TryGetProperty("interactions", out var interactions));
        Assert.Equal(0, interactions.GetArrayLength());
        Assert.Empty(StudioCompositionBodyEditor.ReadBody(envelope).Interactions!);
    }

    [UnitTest]
    public void ReadBody_RoundTripsALayoutBlock()
    {
        using var stored = JsonDocument.Parse(
            """
            {
              "widgets": [{ "id": "area-chart", "kind": "chart" }],
              "layout": { "grid": { "columns": 12 }, "items": [{ "ref": "widget:area-chart", "x": 0, "y": 0, "w": 6, "h": 4 }] }
            }
            """);
        var body = StudioCompositionBodyEditor.ReadBody(BuildEnvelope(StudioPackageFamily.App, stored.RootElement.Clone()));

        Assert.Equal(12, body.Layout!.Grid!.Columns);
        var item = Assert.Single(body.Layout.Items!);
        Assert.Equal("widget:area-chart", item.Ref);
        Assert.Equal(6, item.W);
        Assert.Equal(4, item.H);
    }

    [Theory]
    [InlineData("map", StudioComponentRefResolution.Resolved)]
    [InlineData("layer:parcels", StudioComponentRefResolution.Resolved)]
    [InlineData("widget:area-chart", StudioComponentRefResolution.Resolved)]
    [InlineData("layer:missing", StudioComponentRefResolution.Unresolved)]
    [InlineData("widget:missing", StudioComponentRefResolution.Unresolved)]
    [InlineData("control:year-slider", StudioComponentRefResolution.ControlsUnsupported)]
    [InlineData("layer:", StudioComponentRefResolution.Malformed)]
    [InlineData("parcels", StudioComponentRefResolution.Malformed)]
    [InlineData("", StudioComponentRefResolution.Malformed)]
    [InlineData(null, StudioComponentRefResolution.Malformed)]
    public void ResolveRef_ImplementsTheAdr0030ReferenceGrammar(string? reference, StudioComponentRefResolution expected)
        => Assert.Equal(expected, StudioInteractionVocabulary.ResolveRef(ParcelComposition(), reference));

    [UnitTest]
    public void Vocabulary_ClosedSetsAndBoundsMatchTheStandard()
    {
        Assert.Equal(
            ["featureSelect", "featureHover", "selection", "change", "viewportChange"],
            StudioInteractionVocabulary.EventNames);
        Assert.Equal(
            ["setFilter", "setViewport", "selectFeature", "runWidgetQuery", "setVisibility"],
            StudioInteractionVocabulary.ActionVerbs);
        Assert.Equal(8, StudioInteractionVocabulary.MaxInteractionsPerEventSource);
        Assert.Equal(200, StudioInteractionVocabulary.MaxInteractionIdLength);
        Assert.Equal(1, StudioInteractionVocabulary.MinGridColumns);
        Assert.Equal(24, StudioInteractionVocabulary.MaxGridColumns);
        Assert.Equal(12, StudioInteractionVocabulary.DefaultGridColumns);
    }

    internal static StudioCompositionBody ParcelComposition() => StudioCompositionBody.Empty with
    {
        Layers = [new StudioCompositionLayer { Id = "parcels", Type = "fill" }],
        Widgets = [new StudioCompositionWidget { Id = "area-chart", Kind = "chart" }],
    };

    internal static StudioInteraction SelectParcelFiltersChart()
    {
        using var args = JsonDocument.Parse("""{"field":"parcelId","value":"$event.featureId"}""");
        return new StudioInteraction
        {
            Id = "select-parcel-filters-chart",
            On = new StudioInteractionEvent { Ref = "layer:parcels", Event = "featureSelect" },
            Do = new StudioInteractionAction
            {
                Ref = "widget:area-chart",
                Verb = "setFilter",
                Args = args.RootElement.Clone(),
            },
        };
    }

    private static StudioInteraction Interaction(string id, string eventName) => new()
    {
        Id = id,
        On = new StudioInteractionEvent { Ref = "layer:parcels", Event = eventName },
        Do = new StudioInteractionAction { Ref = "widget:area-chart", Verb = "runWidgetQuery" },
    };

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
