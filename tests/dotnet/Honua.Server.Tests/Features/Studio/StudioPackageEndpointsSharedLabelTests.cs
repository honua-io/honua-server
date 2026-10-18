// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Server.Features.Studio.Models;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Studio;

public sealed partial class StudioPackageEndpointsTests
{
    [IntegrationTest]
    [Endpoint("POST /ogc/styles")]
    [Endpoint("POST /api/v1/studio/package-drafts/{draftId}/style/initialize")]
    [Endpoint("PUT /api/v1/studio/package-drafts/{draftId}")]
    [Endpoint("PUT /api/v1/studio/package-drafts/{draftId}/style")]
    public async Task MapStyle_SharedLabelsPersistAcrossOwnersAndReferenceChanges()
    {
        using var initialBody = JsonDocument.Parse("""
            {"mapPackageId":"shared-label-map","format":"honua_map_package.v1",
             "layers":[{"id":"places","sourceId":"test/0","visible":true},{"id":"other-places","sourceId":"test/0","visible":true}],"widgets":[]}
            """);
        var created = await PostAsync("/api/v1/studio/package-drafts", new CreateStudioPackageDraftRequest
        {
            PackageKey = "shared-label-map",
            Envelope = BuildExportableMapEnvelope() with { Body = initialBody.RootElement.Clone() }
        }, StudioApiJsonContext.Default.CreateStudioPackageDraftRequest);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var draft = await ReadAsync<StudioPackageDraft>(created, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        var route = $"/api/v1/studio/package-drafts/{draft.DraftId:D}";
        var initialized = await PostAsync($"{route}/style/initialize", new InitializeStudioDraftStyleRequest { Generation = draft.Generation },
            StudioApiJsonContext.Default.InitializeStudioDraftStyleRequest);
        initialized.StatusCode.Should().Be(HttpStatusCode.OK);
        draft = await ReadAsync<StudioPackageDraft>(initialized, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        var body = JsonNode.Parse(draft.Envelope.Body!.Value.GetRawText())!.AsObject();
        var target = body["mapSpec"]!["layers"]!.AsArray().OfType<JsonObject>().Single(layer => layer["id"]!.GetValue<string>() == "places");
        var type = target["type"]!.GetValue<string>();
        var styleId = $"studio-shared-{Guid.NewGuid():N}";
        var catalog = new JsonObject
        {
            ["version"] = 8,
            ["sources"] = new JsonObject
            {
                ["catalog"] = new JsonObject
                { ["type"] = "geojson", ["data"] = new JsonObject { ["type"] = "FeatureCollection", ["features"] = new JsonArray() } }
            },
            ["layers"] = new JsonArray(new JsonObject
            {
                ["id"] = "catalog-render",
                ["type"] = type,
                ["source"] = "catalog",
                ["paint"] = new JsonObject { [$"{type}-color"] = "green" }
            })
        };
        catalog["sources"]!["unrelated"] = catalog["sources"]!["catalog"]!.DeepClone();
        catalog["layers"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "catalog-label",
            ["type"] = "symbol",
            ["source"] = "catalog",
            ["layout"] = new JsonObject { ["text-field"] = "Place", ["text-size"] = 14 }
        });
        catalog["layers"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "unrelated-label",
            ["type"] = "symbol",
            ["source"] = "unrelated",
            ["layout"] = new JsonObject { ["text-field"] = "Wrong source" }
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ogc/styles")
        {
            Content = new StringContent(catalog.ToJsonString(), Encoding.UTF8, "application/vnd.mapbox.style+json")
        };
        request.Headers.TryAddWithoutValidation("X-Style-Id", styleId);
        using var catalogCreated = await _client.SendAsync(request);
        catalogCreated.StatusCode.Should().Be(HttpStatusCode.Created);

        body["mapSpec"]!["layers"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "manual-label",
            ["type"] = "symbol",
            ["source"] = target["source"]!.DeepClone(),
            ["metadata"] = new JsonObject { ["honua:compositionLayerId"] = "places" },
            ["layout"] = new JsonObject { ["text-field"] = "Map-local label" }
        });
        draft = await SaveBody(draft, body);
        body = JsonNode.Parse(draft.Envelope.Body!.Value.GetRawText())!.AsObject();
        body["layers"]![0]!["styleRef"] = styleId;
        body["styleRefs"] = new JsonArray(new JsonObject
        {
            ["styleId"] = styleId,
            ["layerBindings"] = new JsonArray(new JsonObject
            {
                ["styleLayerId"] = "catalog-render",
                ["targetLayerId"] = "places",
                ["layerType"] = type
            })
        });
        // Existing explicit subsets stay explicit until the user reapplies the
        // shared style through the same canonical layer-style operation.
        draft = await SaveBody(draft, body);
        draft.Envelope.Body!.Value.GetProperty("styleRefs")[0].GetProperty("layerBindings").GetArrayLength().Should().Be(1);
        var rebound = StudioCompositionBodyEditor.SetLayerStyleRef(
            StudioCompositionBodyEditor.ReadBody(draft.Envelope), "places", styleId);
        var reboundEnvelope = StudioCompositionBodyEditor.WriteBody(draft.Envelope, rebound);
        body = JsonNode.Parse(reboundEnvelope.Body!.Value.GetRawText())!.AsObject();
        using var styledBody = JsonDocument.Parse(body.ToJsonString());
        var savedResponse = await PutAsync(route, new UpdateStudioPackageDraftRequest
        {
            PackageKey = draft.PackageKey,
            Generation = draft.Generation,
            Envelope = draft.Envelope with { Body = styledBody.RootElement.Clone() }
        }, StudioApiJsonContext.Default.UpdateStudioPackageDraftRequest);
        savedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var saved = await ReadAsync<StudioPackageDraft>(savedResponse, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        var reference = saved.Envelope.Body!.Value.GetProperty("styleRefs")[0];
        reference.TryGetProperty("body", out _).Should().BeFalse("a live reference must not freeze its catalog bytes");
        var mapping = reference.GetProperty("layerBindings")[0];
        mapping.GetProperty("styleLayerId").GetString().Should().Be("catalog-render");
        mapping.GetProperty("targetLayerId").GetString().Should().Be("places");
        mapping.GetProperty("layerType").GetString().Should().Be(type);

        var firstBindings = reference.GetProperty("layerBindings").EnumerateArray().ToArray();
        firstBindings.Should().NotContain(binding => binding.GetProperty("styleLayerId").GetString() == "unrelated-label");
        var firstLabelId = firstBindings.Single(binding => binding.GetProperty("styleLayerId").GetString() == "catalog-label")
            .GetProperty("targetLayerId").GetString()!;
        var firstLabel = saved.Envelope.Body.Value.GetProperty("mapSpec").GetProperty("layers").EnumerateArray()
            .Single(layer => layer.GetProperty("id").GetString() == firstLabelId);
        firstLabel.GetProperty("type").GetString().Should().Be("symbol");
        firstLabel.GetProperty("source").GetString().Should().Be(target["source"]!.GetValue<string>());
        firstLabel.GetProperty("metadata").GetProperty("honua:compositionLayerId").GetString().Should().Be("places");
        firstLabel.TryGetProperty("layout", out _).Should().BeFalse("the shared label must stay live, without frozen text or appearance");

        // A new owner reuses the same reference; existing bindings are preserved
        // while the new owner receives its own canonical label target.
        var secondBody = JsonNode.Parse(saved.Envelope.Body.Value.GetRawText())!.AsObject();
        secondBody["layers"]![1]!["styleRef"] = styleId;
        saved = await SaveBody(saved, secondBody);
        var bothBindings = saved.Envelope.Body!.Value.GetProperty("styleRefs")[0].GetProperty("layerBindings").EnumerateArray().ToArray();
        bothBindings.Should().Contain(binding => binding.GetProperty("targetLayerId").GetString() == firstLabelId);
        var labelIds = bothBindings.Where(binding => binding.GetProperty("styleLayerId").GetString() == "catalog-label")
            .Select(binding => binding.GetProperty("targetLayerId").GetString()!).ToArray();
        labelIds.Should().HaveCount(2).And.OnlyHaveUniqueItems();
        var secondLabelId = labelIds.Single(id => id != firstLabelId);
        var refreshedOwner = StudioCompositionBodyEditor.SetLayerStyleRef(
            StudioCompositionBodyEditor.ReadBody(saved.Envelope), "places", styleId);
        var refreshEnvelope = StudioCompositionBodyEditor.WriteBody(saved.Envelope, refreshedOwner);
        saved = await SaveBody(saved, JsonNode.Parse(refreshEnvelope.Body!.Value.GetRawText())!.AsObject());
        var refreshedBindings = saved.Envelope.Body!.Value.GetProperty("styleRefs")[0].GetProperty("layerBindings").EnumerateArray().ToArray();
        refreshedBindings.Should().HaveCount(bothBindings.Length);
        refreshedBindings.Should().Contain(binding => binding.GetProperty("targetLayerId").GetString() == firstLabelId)
            .And.Contain(binding => binding.GetProperty("targetLayerId").GetString() == secondLabelId);
        saved.Envelope.Body.Value.GetProperty("mapSpec").GetProperty("layers").EnumerateArray()
            .Count(layer => layer.GetProperty("id").GetString() == firstLabelId).Should().Be(1);
        var stableBody = saved.Envelope.Body.Value.Clone();
        saved = await SaveBody(saved, JsonNode.Parse(stableBody.GetRawText())!.AsObject());
        JsonElement.DeepEquals(saved.Envelope.Body!.Value, stableBody).Should().BeTrue("repeated saves must not duplicate or hydrate targets");

        // Explicit snapshot intent must preserve targets even when the supplied
        // map document is byte-for-byte identical to the unhydrated base.
        var snapshotResponse = await PutAsync($"{route}/style", new UpdateStudioDraftStyleRequest
        {
            Generation = saved.Generation,
            MapSpec = saved.Envelope.Body.Value.GetProperty("mapSpec").Clone()
        }, StudioApiJsonContext.Default.UpdateStudioDraftStyleRequest);
        snapshotResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var snapshot = await ReadAsync<StudioPackageDraft>(snapshotResponse, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        snapshot.Envelope.Body!.Value.GetProperty("mapSpec").GetProperty("layers").EnumerateArray()
            .Select(layer => layer.GetProperty("id").GetString()).Should().Contain(firstLabelId).And.Contain(secondLabelId);
        snapshot.Envelope.Body.Value.GetProperty("styleRefs").GetArrayLength().Should().Be(0);
        saved = await SaveBody(snapshot, JsonNode.Parse(stableBody.GetRawText())!.AsObject());

        // Clear one owner without removing the other owner's live reference.
        var cleared = JsonNode.Parse(saved.Envelope.Body!.Value.GetRawText())!.AsObject();
        cleared["layers"]![0]!.AsObject().Remove("styleRef");
        saved = await SaveBody(saved, cleared);
        var remainingIds = saved.Envelope.Body!.Value.GetProperty("mapSpec").GetProperty("layers").EnumerateArray()
            .Select(layer => layer.GetProperty("id").GetString()).ToArray();
        remainingIds.Should().NotContain(firstLabelId).And.Contain(secondLabelId).And.Contain("places").And.Contain("manual-label");
        var remainingBindings = saved.Envelope.Body.Value.GetProperty("styleRefs")[0].GetProperty("layerBindings").EnumerateArray().ToArray();
        remainingBindings.Should().NotContain(binding => binding.GetProperty("targetLayerId").GetString() == firstLabelId
            || binding.GetProperty("targetLayerId").GetString() == "places");
        remainingBindings.Should().Contain(binding => binding.GetProperty("targetLayerId").GetString() == secondLabelId);

        var reopenedResponse = await _client.GetAsync(route);
        reopenedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var reopened = await ReadAsync<StudioPackageDraft>(reopenedResponse, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        reopened.Generation.Should().Be(saved.Generation);
        JsonElement.DeepEquals(reopened.Envelope.Body!.Value, saved.Envelope.Body.Value).Should().BeTrue();

        async Task<StudioPackageDraft> SaveBody(StudioPackageDraft current, JsonObject candidate)
        {
            using var document = JsonDocument.Parse(candidate.ToJsonString());
            var response = await PutAsync(route, new UpdateStudioPackageDraftRequest
            {
                PackageKey = current.PackageKey,
                Generation = current.Generation,
                Envelope = current.Envelope with { Body = document.RootElement.Clone() }
            }, StudioApiJsonContext.Default.UpdateStudioPackageDraftRequest);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            return await ReadAsync<StudioPackageDraft>(response, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        }
    }
}
