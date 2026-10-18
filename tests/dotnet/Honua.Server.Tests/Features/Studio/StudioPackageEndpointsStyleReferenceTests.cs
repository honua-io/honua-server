// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.Studio.Domain;
using Honua.Server.Features.Studio.Models;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Studio;

public sealed partial class StudioPackageEndpointsTests
{
    [IntegrationTest]
    [Endpoint("POST /ogc/styles")]
    [Endpoint("POST /api/v1/studio/package-drafts/{draftId}/style/initialize")]
    [Endpoint("PUT /api/v1/studio/package-drafts/{draftId}")]
    public async Task MapStyle_SharedBindingsUseActualCatalogAndRejectStaleOrInvalidOverrides()
    {
        using var initialBody = JsonDocument.Parse("""
            {"mapPackageId":"style-ref-map","format":"honua_map_package.v1",
             "layers":[{"id":"places","sourceId":"test/0","visible":true}],"widgets":[]}
            """);
        var created = await PostAsync("/api/v1/studio/package-drafts", new CreateStudioPackageDraftRequest
        {
            PackageKey = "style-ref-map",
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
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ogc/styles")
        {
            Content = new StringContent(catalog.ToJsonString(), Encoding.UTF8, "application/vnd.mapbox.style+json")
        };
        request.Headers.TryAddWithoutValidation("X-Style-Id", styleId);
        using var catalogCreated = await _client.SendAsync(request);
        catalogCreated.StatusCode.Should().Be(HttpStatusCode.Created);

        body["layers"]![0]!["styleRef"] = styleId;
        body["styleRefs"] = new JsonArray(new JsonObject { ["styleId"] = styleId });
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

        foreach (var defect in new[] { "stale-catalog-layer", "foreign-target", "invalid-appearance", "missing-field" })
        {
            var invalid = JsonNode.Parse(saved.Envelope.Body.Value.GetRawText())!.AsObject();
            var invalidRef = invalid["styleRefs"]![0]!.AsObject();
            if (defect == "stale-catalog-layer") invalidRef["layerBindings"]![0]!["styleLayerId"] = "deleted-render";
            else if (defect == "foreign-target") invalidRef["layerBindings"]![0]!["targetLayerId"] = "honua-basemap:background";
            else
            {
                invalidRef.Remove("layerBindings");
                invalidRef["body"] = new JsonObject
                {
                    ["places"] = defect == "invalid-appearance"
                    ? new JsonObject { ["minzoom"] = "invalid" }
                    : new JsonObject { ["filter"] = new JsonArray("==", new JsonArray("get", "unavailable_field_83621"), "R") }
                };
            }
            using var invalidDocument = JsonDocument.Parse(invalid.ToJsonString());
            var rejected = await PutAsync(route, new UpdateStudioPackageDraftRequest
            {
                PackageKey = saved.PackageKey,
                Generation = saved.Generation,
                Envelope = saved.Envelope with { Body = invalidDocument.RootElement.Clone() }
            }, StudioApiJsonContext.Default.UpdateStudioPackageDraftRequest);
            rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest, defect);
        }
        var reopenedResponse = await _client.GetAsync(route);
        reopenedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var reopened = await ReadAsync<StudioPackageDraft>(reopenedResponse, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        reopened.Generation.Should().Be(saved.Generation);
        JsonElement.DeepEquals(reopened.Envelope.Body!.Value, saved.Envelope.Body.Value).Should().BeTrue();
    }
}
