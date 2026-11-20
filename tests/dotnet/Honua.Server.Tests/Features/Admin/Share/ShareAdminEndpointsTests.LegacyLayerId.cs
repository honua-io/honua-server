// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Admin.Share;

// Published clients still send the deprecated layerId name for the service layer index, in the
// export definition body and in the list filter.
public sealed partial class ShareAdminEndpointsTests
{
    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/share/exports")]
    [Endpoint("GET /api/v1/admin/share/exports")]
    public async Task ExportDefinition_DeprecatedLayerIdName_IsAcceptedInBodyAndFilter()
    {
        var created = await _client.PostAsJsonAsync("/api/v1/admin/share/exports", LegacyRequest("legacy-layer", layerId: 7), JsonOptions);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        using (var doc = await ReadJsonAsync(created))
        {
            doc.RootElement.GetProperty("serviceLayerIndex").GetInt32().Should().Be(7);
            doc.RootElement.GetProperty("layerId").GetInt32().Should().Be(7);
        }

        (await ListServiceNamesAsync("serviceName=legacy-layer&layerId=7")).Should().Contain("legacy-layer");
        (await ListServiceNamesAsync("serviceName=legacy-layer&layerId=8")).Should().BeEmpty("the deprecated filter is applied, not ignored");
        (await _client.GetAsync("/api/v1/admin/share/exports?serviceLayerIndex=7&layerId=8")).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/share/exports")]
    public async Task ExportDefinition_MissingOrConflictingLayerIndex_IsRejected()
    {
        var missing = await _client.PostAsJsonAsync("/api/v1/admin/share/exports", LegacyRequest("no-layer", layerId: null), JsonOptions);
        var conflicting = await _client.PostAsJsonAsync(
            "/api/v1/admin/share/exports", LegacyRequest("two-layers", layerId: 8, serviceLayerIndex: 7), JsonOptions);

        missing.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await missing.Content.ReadAsStringAsync()).Should().Contain("serviceLayerIndex is required");
        conflicting.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await conflicting.Content.ReadAsStringAsync()).Should().Contain("disagree");
    }

    private async Task<List<string?>> ListServiceNamesAsync(string query)
    {
        var response = await _client.GetAsync($"/api/v1/admin/share/exports?{query}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = await ReadJsonAsync(response);
        return doc.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("serviceName").GetString())
            .ToList();
    }

    private static Dictionary<string, object?> LegacyRequest(string serviceName, int? layerId, int? serviceLayerIndex = null)
    {
        var request = new Dictionary<string, object?>
        {
            ["serviceName"] = serviceName,
            ["destinationType"] = "Webhook",
            ["destinationConfig"] = new Dictionary<string, string>
            {
                ["url"] = "https://example.invalid/share-webhook",
                ["credentialRef"] = "vault://share/webhook",
            },
            ["format"] = "GeoJSON",
            ["schedule"] = "0 * * * *",
        };
        if (layerId is { } legacy)
        {
            request["layerId"] = legacy;
        }

        if (serviceLayerIndex is { } index)
        {
            request["serviceLayerIndex"] = index;
        }

        return request;
    }
}
