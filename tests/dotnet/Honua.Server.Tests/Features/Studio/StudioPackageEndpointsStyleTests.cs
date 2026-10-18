// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Server.Features.Studio.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Studio;

public sealed partial class StudioPackageEndpointsTests
{
    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/package-drafts/{draftId}/style/initialize")]
    [Endpoint("PUT /api/v1/studio/package-drafts/{draftId}/style")]
    [Endpoint("PUT /api/v1/studio/package-drafts/{draftId}")]
    public async Task MapStyle_PublishedSourceIsResolved_RevocationBlocksStylingButAllowsRemoval()
    {
        using var document = JsonDocument.Parse("""
            {"mapPackageId":"published-style","format":"honua_map_package.v1",
             "layers":[{"id":"places","sourceId":"test/0","visible":true}],"widgets":[],
             "sourceBindings":[{"sourceId":"test/0","protocol":"geoservices_feature_service",
               "locator":{"url":"https://stale.invalid/FeatureServer/99","layerId":99},"attribution":"Survey team"}]}
            """);
        var created = await PostAsync("/api/v1/studio/package-drafts", new CreateStudioPackageDraftRequest
        {
            PackageKey = "published-style",
            Envelope = BuildExportableMapEnvelope() with { Body = document.RootElement.Clone() }
        }, StudioApiJsonContext.Default.CreateStudioPackageDraftRequest);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var draft = await ReadAsync<StudioPackageDraft>(created, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        var route = $"/api/v1/studio/package-drafts/{draft.DraftId:D}";
        var initialized = await PostAsync($"{route}/style/initialize",
            new InitializeStudioDraftStyleRequest { Generation = draft.Generation },
            StudioApiJsonContext.Default.InitializeStudioDraftStyleRequest);
        initialized.StatusCode.Should().Be(HttpStatusCode.OK);
        var saved = await ReadAsync<StudioPackageDraft>(initialized, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        var map = saved.Envelope.Body!.Value.GetProperty("mapSpec");
        var serviceName = _fixture.GetCurrentV2GraphSnapshot().Graph.Services
            .Single(service => service.Metadata.Id == WebAppFixture.TestServiceId).Metadata.Name;
        var servicePath = string.Join('/', serviceName.Split('/').Select(Uri.EscapeDataString));
        map.GetProperty("sources").GetProperty("test/0").GetProperty("data").GetString()
            .Should().Be($"/rest/services/{servicePath}/FeatureServer/0/query?where=1%3D1&outFields=*&f=geojson&resultRecordCount=2000");
        map.GetProperty("layers").EnumerateArray().Should().Contain(layer =>
            layer.GetProperty("id").GetString() == "places" && layer.GetProperty("source").GetString() == "test/0");
        var binding = saved.Envelope.Body.Value.GetProperty("sourceBindings")[0];
        binding.GetProperty("locator").GetProperty("url").GetString()
            .Should().Be($"/rest/services/{servicePath}/FeatureServer/0");
        binding.GetProperty("locator").GetProperty("layerId").GetInt32().Should().Be(0);
        binding.GetProperty("attribution").GetString().Should().Be("Survey team");

        var styled = await PutAsync($"{route}/style", new UpdateStudioDraftStyleRequest
        {
            Generation = saved.Generation,
            MapSpec = map.Clone()
        }, StudioApiJsonContext.Default.UpdateStudioDraftStyleRequest);
        styled.StatusCode.Should().Be(HttpStatusCode.OK);
        saved = await ReadAsync<StudioPackageDraft>(styled, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);

        // A whole-draft writer cannot retarget the portable binding while leaving
        // mapSpec untouched: the published SDK consumes these bindings on reopen.
        var changedBody = JsonNode.Parse(saved.Envelope.Body!.Value.GetRawText())!.AsObject();
        changedBody["sourceBindings"]![0]!["locator"]!["url"] = "https://wrong.invalid/FeatureServer/9";
        changedBody["sourceBindings"]![0]!["locator"]!["layerId"] = 9;
        using var changedDocument = JsonDocument.Parse(changedBody.ToJsonString());
        var normalized = await PutAsync(route, new UpdateStudioPackageDraftRequest
        {
            PackageKey = saved.PackageKey,
            Envelope = saved.Envelope with { Body = changedDocument.RootElement.Clone() },
            Generation = saved.Generation
        }, StudioApiJsonContext.Default.UpdateStudioPackageDraftRequest);
        normalized.StatusCode.Should().Be(HttpStatusCode.OK);
        saved = await ReadAsync<StudioPackageDraft>(normalized, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        saved.Envelope.Body!.Value.GetProperty("sourceBindings")[0].GetProperty("locator")
            .GetProperty("url").GetString().Should().Be($"/rest/services/{servicePath}/FeatureServer/0");

        _fixture.SetV2LayerEnabled(WebAppFixture.TestLayerId, false);
        var denied = await PutAsync($"{route}/style", new UpdateStudioDraftStyleRequest
        {
            Generation = saved.Generation,
            MapSpec = map.Clone()
        }, StudioApiJsonContext.Default.UpdateStudioDraftStyleRequest);
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var genericDenied = await PutAsync(route, new UpdateStudioPackageDraftRequest
        {
            PackageKey = saved.PackageKey,
            Envelope = saved.Envelope,
            Generation = saved.Generation
        }, StudioApiJsonContext.Default.UpdateStudioPackageDraftRequest);
        genericDenied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var unchanged = await ReadAsync<StudioPackageDraft>(await _client.GetAsync(route),
            StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        unchanged.Generation.Should().Be(saved.Generation);

        // Manual layer removal arrives through the generic draft route. It must
        // remove stale render sources without requiring access to the removed layer.
        var body = StudioCompositionBodyEditor.ReadBody(saved.Envelope) with { Layers = [] };
        var removed = await PutAsync(route, new UpdateStudioPackageDraftRequest
        {
            PackageKey = saved.PackageKey,
            Envelope = StudioCompositionBodyEditor.WriteBody(saved.Envelope, body),
            Generation = saved.Generation
        }, StudioApiJsonContext.Default.UpdateStudioPackageDraftRequest);
        removed.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadAsync<StudioPackageDraft>(removed, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        result.Generation.Should().Be(saved.Generation + 1);
        var finalMap = result.Envelope.Body!.Value.GetProperty("mapSpec");
        finalMap.GetProperty("sources").EnumerateObject().Should().BeEmpty();
        result.Envelope.Body.Value.GetProperty("sourceBindings").EnumerateArray().Should().BeEmpty();
        finalMap.GetProperty("layers").EnumerateArray().Should().OnlyContain(layer =>
            layer.GetProperty("type").GetString() == "background");
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/studio/package-drafts/{draftId}")]
    public async Task MapStyle_WholeDraftUpdateCannotBypassAppearanceOrAssetValidation()
    {
        using var document = JsonDocument.Parse("""
            {"mapPackageId":"whole-style","format":"honua_map_package.v1","layers":[],"widgets":[],
             "mapSpec":{"version":8,"sources":{},"layers":[
               {"id":"background","type":"background","paint":{"background-color":"#ffffff"}}]}}
            """);
        var created = await PostAsync("/api/v1/studio/package-drafts", new CreateStudioPackageDraftRequest
        {
            PackageKey = "whole-style",
            Envelope = BuildExportableMapEnvelope() with { Body = document.RootElement.Clone() }
        }, StudioApiJsonContext.Default.CreateStudioPackageDraftRequest);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var draft = await ReadAsync<StudioPackageDraft>(created, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        var route = $"/api/v1/studio/package-drafts/{draft.DraftId:D}";
        var body = JsonNode.Parse(document.RootElement.GetRawText())!.AsObject();
        body["mapSpec"]!["layers"]![0]!["paint"]!["background-color"] = "#112233";
        using var appearance = JsonDocument.Parse(body.ToJsonString());
        var saved = await PutAsync(route, new UpdateStudioPackageDraftRequest
        {
            PackageKey = draft.PackageKey,
            Envelope = draft.Envelope with { Body = appearance.RootElement.Clone() },
            Generation = draft.Generation
        }, StudioApiJsonContext.Default.UpdateStudioPackageDraftRequest);
        saved.StatusCode.Should().Be(HttpStatusCode.OK);
        draft = await ReadAsync<StudioPackageDraft>(saved, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        draft.Envelope.Body!.Value.GetProperty("mapSpec").GetProperty("layers")[0]
            .GetProperty("paint").GetProperty("background-color").GetString().Should().Be("#112233");

        foreach (var member in new[] { "glyphs", "version", "mapSpec" })
        {
            var candidate = body.DeepClone().AsObject();
            if (member == "mapSpec") candidate.Remove("mapSpec");
            else if (member == "version") candidate["mapSpec"]!["version"] = 7;
            else candidate["mapSpec"]!["glyphs"] = "https://wrong.invalid/{fontstack}/{range}.pbf";
            using var invalid = JsonDocument.Parse(candidate.ToJsonString());
            var rejected = await PutAsync(route, new UpdateStudioPackageDraftRequest
            {
                PackageKey = draft.PackageKey,
                Envelope = draft.Envelope with { Body = invalid.RootElement.Clone() },
                Generation = draft.Generation
            }, StudioApiJsonContext.Default.UpdateStudioPackageDraftRequest);
            rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"whole-draft changes to {member} need the style policy");
        }
        var reopened = await ReadAsync<StudioPackageDraft>(await _client.GetAsync(route),
            StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        reopened.Generation.Should().Be(draft.Generation);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/package-drafts/{draftId}/style/initialize")]
    public async Task MapStyle_InitializeEmptyDraft_CreatesDurableVersionedBackground()
    {
        using var document = JsonDocument.Parse("""
            {"mapPackageId":"initial-style","format":"honua_map_package.v1","layers":[],"widgets":[]}
            """);
        var created = await PostAsync("/api/v1/studio/package-drafts", new CreateStudioPackageDraftRequest
        {
            PackageKey = "initial-style",
            Envelope = BuildExportableMapEnvelope() with { Body = document.RootElement.Clone() }
        }, StudioApiJsonContext.Default.CreateStudioPackageDraftRequest);
        var draft = await ReadAsync<StudioPackageDraft>(created, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        var initialized = await PostAsync($"/api/v1/studio/package-drafts/{draft.DraftId:D}/style/initialize",
            new InitializeStudioDraftStyleRequest { Generation = draft.Generation },
            StudioApiJsonContext.Default.InitializeStudioDraftStyleRequest);
        initialized.StatusCode.Should().Be(HttpStatusCode.OK);
        var saved = await ReadAsync<StudioPackageDraft>(initialized, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        saved.Generation.Should().Be(draft.Generation + 1);
        var map = saved.Envelope.Body!.Value.GetProperty("mapSpec");
        map.GetProperty("version").GetInt32().Should().Be(8);
        map.GetProperty("sources").EnumerateObject().Should().BeEmpty();
        map.GetProperty("layers")[0].GetProperty("type").GetString().Should().Be("background");
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/studio/package-drafts/{draftId}/style")]
    public async Task MapStyle_SavePersistsAppearance_RejectsSourceChangesAndStaleGeneration()
    {
        using var document = JsonDocument.Parse("""
            {"mapPackageId":"style-roundtrip","format":"honua_map_package.v1",
             "layers":[],"widgets":[],"styleRefs":[],
             "mapSpec":{"version":8,"sources":{},"layers":[
               {"id":"background","type":"background","paint":{"background-color":"#ffffff"}}]}}
            """);
        var created = await PostAsync("/api/v1/studio/package-drafts", new CreateStudioPackageDraftRequest
        {
            PackageKey = "style-roundtrip",
            Envelope = BuildExportableMapEnvelope() with { Body = document.RootElement.Clone() }
        }, StudioApiJsonContext.Default.CreateStudioPackageDraftRequest);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var draft = await ReadAsync<StudioPackageDraft>(created, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        var map = JsonNode.Parse(document.RootElement.GetProperty("mapSpec").GetRawText())!.AsObject();
        map["layers"]![0]!["paint"]!["background-color"] = "#112233";
        using var appearance = JsonDocument.Parse(map.ToJsonString());
        var next = new UpdateStudioDraftStyleRequest
        {
            Generation = draft.Generation,
            MapSpec = appearance.RootElement.Clone()
        };
        var route = $"/api/v1/studio/package-drafts/{draft.DraftId:D}/style";
        var saved = await PutAsync(route, next, StudioApiJsonContext.Default.UpdateStudioDraftStyleRequest);
        saved.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await ReadAsync<StudioPackageDraft>(saved, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        updated.Generation.Should().Be(draft.Generation + 1);
        updated.Envelope.Body!.Value.GetProperty("mapSpec").GetProperty("layers")[0]
            .GetProperty("paint").GetProperty("background-color").GetString().Should().Be("#112233");

        var stale = await PutAsync(route, next, StudioApiJsonContext.Default.UpdateStudioDraftStyleRequest);
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);

        map["sources"]!["foreign"] = new JsonObject { ["type"] = "geojson", ["data"] = "https://example.invalid/private" };
        using var changedSource = JsonDocument.Parse(map.ToJsonString());
        var rejected = await PutAsync(route, next with
        {
            Generation = updated.Generation,
            MapSpec = changedSource.RootElement.Clone()
        }, StudioApiJsonContext.Default.UpdateStudioDraftStyleRequest);
        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var reopened = await ReadAsync<StudioPackageDraft>(
            await _client.GetAsync($"/api/v1/studio/package-drafts/{draft.DraftId:D}"),
            StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        reopened.Generation.Should().Be(updated.Generation);
        reopened.Envelope.Body!.Value.GetProperty("mapSpec").GetProperty("sources").EnumerateObject().Should().BeEmpty();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/package-drafts/{draftId}/style/initialize")]
    public async Task MapStyle_InitializeRejectsUnknownPublishedIdentity_WithoutChangingDraft()
    {
        using var document = JsonDocument.Parse("""
            {"mapPackageId":"unknown-source","format":"honua_map_package.v1",
             "layers":[{"id":"parcels","sourceId":"missing-service/7","visible":true}],"widgets":[]}
            """);
        var created = await PostAsync("/api/v1/studio/package-drafts", new CreateStudioPackageDraftRequest
        {
            PackageKey = "unknown-style-source",
            Envelope = BuildExportableMapEnvelope() with { Body = document.RootElement.Clone() }
        }, StudioApiJsonContext.Default.CreateStudioPackageDraftRequest);
        var draft = await ReadAsync<StudioPackageDraft>(created, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        var response = await PostAsync($"/api/v1/studio/package-drafts/{draft.DraftId:D}/style/initialize",
            new InitializeStudioDraftStyleRequest { Generation = draft.Generation },
            StudioApiJsonContext.Default.InitializeStudioDraftStyleRequest);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var reopened = await ReadAsync<StudioPackageDraft>(
            await _client.GetAsync($"/api/v1/studio/package-drafts/{draft.DraftId:D}"),
            StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        reopened.Generation.Should().Be(draft.Generation);
        reopened.Envelope.Body!.Value.TryGetProperty("mapSpec", out _).Should().BeFalse();
    }
}
