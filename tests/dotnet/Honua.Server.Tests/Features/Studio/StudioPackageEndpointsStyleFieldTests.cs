// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
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
    [Endpoint("POST /api/v1/studio/package-drafts/{draftId}/style/initialize")]
    [Endpoint("PUT /api/v1/studio/package-drafts/{draftId}/style")]
    [Endpoint("PUT /api/v1/studio/package-drafts/{draftId}")]
    public async Task MapStyle_LocalAppearanceAndLabelsRequireActualPublishedFields()
    {
        using var initialBody = JsonDocument.Parse("""
            {"mapPackageId":"style-fields-map","format":"honua_map_package.v1",
             "layers":[{"id":"places","sourceId":"test/0","visible":true}],"widgets":[]}
            """);
        var created = await PostAsync("/api/v1/studio/package-drafts", new CreateStudioPackageDraftRequest
        {
            PackageKey = "style-fields-map",
            Envelope = BuildExportableMapEnvelope() with { Body = initialBody.RootElement.Clone() }
        }, StudioApiJsonContext.Default.CreateStudioPackageDraftRequest);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var draft = await ReadAsync<StudioPackageDraft>(created, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        var route = $"/api/v1/studio/package-drafts/{draft.DraftId:D}";
        var initialized = await PostAsync($"{route}/style/initialize", new InitializeStudioDraftStyleRequest { Generation = draft.Generation },
            StudioApiJsonContext.Default.InitializeStudioDraftStyleRequest);
        initialized.StatusCode.Should().Be(HttpStatusCode.OK);
        draft = await ReadAsync<StudioPackageDraft>(initialized, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);

        using var metadataResponse = await _client.GetAsync("/rest/services/test/FeatureServer/0?f=json");
        metadataResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var metadata = JsonDocument.Parse(await metadataResponse.Content.ReadAsStringAsync());
        var actualField = metadata.RootElement.GetProperty("fields").EnumerateArray()
            .Select(field => field.GetProperty("name").GetString()!).First();

        foreach (var appearanceKind in new[] { "filter", "label", "label-token", "explicit-properties" })
        {
            foreach (var typed in new[] { false, true })
            {
                var body = JsonNode.Parse(draft.Envelope.Body!.Value.GetRawText())!.AsObject();
                var style = body["mapSpec"]!.AsObject();
                var renders = style["layers"]!.AsArray();
                var primary = renders.OfType<JsonObject>().Single(layer => layer["id"]!.GetValue<string>() == "places");
                if (appearanceKind is "label" or "label-token")
                {
                    var label = BuildLabel(primary, "unavailable_field_83621");
                    if (appearanceKind == "label-token") label["layout"]!["text-field"] = "Parcel {unavailable_field_83621}";
                    renders.Add(label);
                }
                else
                    primary["filter"] = new JsonArray("==", appearanceKind == "explicit-properties"
                        ? new JsonArray("get", "unavailable_field_83621", new JsonArray("properties"))
                        : new JsonArray("get", "unavailable_field_83621"), "R");
                using var proposed = JsonDocument.Parse(body.ToJsonString());
                var rejected = typed
                    ? await PutAsync($"{route}/style", new UpdateStudioDraftStyleRequest
                    {
                        Generation = draft.Generation,
                        MapSpec = proposed.RootElement.GetProperty("mapSpec").Clone()
                    }, StudioApiJsonContext.Default.UpdateStudioDraftStyleRequest)
                    : await PutAsync(route, new UpdateStudioPackageDraftRequest
                    {
                        PackageKey = draft.PackageKey,
                        Generation = draft.Generation,
                        Envelope = draft.Envelope with { Body = proposed.RootElement.Clone() }
                    }, StudioApiJsonContext.Default.UpdateStudioPackageDraftRequest);
                rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"appearance={appearanceKind}, typed={typed}");
            }
        }
        var unchangedResponse = await _client.GetAsync(route);
        unchangedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var unchanged = await ReadAsync<StudioPackageDraft>(unchangedResponse, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        unchanged.Generation.Should().Be(draft.Generation);
        JsonElement.DeepEquals(unchanged.Envelope.Body!.Value, draft.Envelope.Body!.Value).Should().BeTrue();

        var validStyle = JsonNode.Parse(draft.Envelope.Body!.Value.GetProperty("mapSpec").GetRawText())!.AsObject();
        var validRenders = validStyle["layers"]!.AsArray();
        var validPrimary = validRenders.OfType<JsonObject>().Single(layer => layer["id"]!.GetValue<string>() == "places");
        validPrimary["filter"] = new JsonArray("all",
            new JsonArray("has", "literal_only_name", new JsonArray("literal", new JsonObject { ["literal_only_name"] = true })),
            new JsonArray("in", "literal_value", new JsonArray("literal", new JsonArray("literal_value"))));
        validRenders.Add(BuildLabel(validPrimary, actualField));
        using var validDocument = JsonDocument.Parse(validStyle.ToJsonString());
        var savedResponse = await PutAsync($"{route}/style", new UpdateStudioDraftStyleRequest
        {
            Generation = draft.Generation,
            MapSpec = validDocument.RootElement.Clone()
        }, StudioApiJsonContext.Default.UpdateStudioDraftStyleRequest);
        savedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var saved = await ReadAsync<StudioPackageDraft>(savedResponse, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);
        saved.Generation.Should().BeGreaterThan(draft.Generation);
        var savedLabel = saved.Envelope.Body!.Value.GetProperty("mapSpec").GetProperty("layers").EnumerateArray()
            .Single(layer => layer.GetProperty("id").GetString() == "places-label");
        savedLabel.GetProperty("layout").GetProperty("text-field")[1][1].GetString().Should().Be(actualField);

        static JsonObject BuildLabel(JsonObject primary, string field) => new()
        {
            ["id"] = "places-label",
            ["type"] = "symbol",
            ["source"] = primary["source"]!.DeepClone(),
            ["metadata"] = primary["metadata"]!.DeepClone(),
            ["layout"] = new JsonObject { ["text-field"] = new JsonArray("to-string", new JsonArray("get", field)), ["text-size"] = 14 },
            ["paint"] = new JsonObject { ["text-color"] = "#111111" }
        };
    }
}
