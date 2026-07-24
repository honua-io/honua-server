// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.TestKit.Attributes;

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

    [Theory]
    [InlineData(StudioPackageFamily.Query)]
    [InlineData(StudioPackageFamily.Dashboard)]
    [InlineData(StudioPackageFamily.Report)]
    [InlineData(StudioPackageFamily.Form)]
    [InlineData(StudioPackageFamily.Workflow)]
    [InlineData(StudioPackageFamily.Geoprocessing)]
    [InlineData(StudioPackageFamily.Etl)]
    [InlineData(StudioPackageFamily.Analysis)]
    public void EnsureCompositionEligibleFamily_RejectsNonMapAppFamilies(StudioPackageFamily family)
        => Assert.Throws<StudioCompositionFamilyException>(
            () => StudioCompositionBodyEditor.EnsureCompositionEligibleFamily(family));

    [Theory]
    [InlineData(StudioPackageFamily.Map)]
    [InlineData(StudioPackageFamily.App)]
    public void EnsureCompositionEligibleFamily_AllowsMapAndApp(StudioPackageFamily family)
        => StudioCompositionBodyEditor.EnsureCompositionEligibleFamily(family);

    private static StudioPackageEnvelope BuildEnvelope(StudioPackageFamily family, System.Text.Json.JsonElement? body)
        => new()
        {
            Family = family,
            SchemaVersion = "1.0",
            Body = body,
        };
}
