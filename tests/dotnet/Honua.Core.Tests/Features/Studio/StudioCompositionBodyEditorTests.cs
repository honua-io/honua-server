// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
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

    [UnitTest]
    public void ReadBody_ThenWriteBody_PreservesFieldsOutsideTheProjection()
    {
        // StudioCompositionBody is a PROJECTION, not the whole document: a canonical map package
        // also carries mapPackageId/format/status/createdAt/sourceBindings/initialView, which
        // MapGenerationSchema requires. Both round trips that serialize this record back over the
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
